using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// chart_info backfill 中に補完した MD5/SHA-256 対応です。
/// BMSFile へ SHA-256 を反映する前に DB へ保存できるよう、所有オブジェクトとは分離しています。
/// </summary>
/// <remarks>
/// 保存する digest 対応を作成します。
/// </remarks>
/// <param name="md5">LR2 song.hash と対応する MD5。</param>
/// <param name="sha256">譜面ファイル全体の SHA-256。</param>
internal sealed class ChartDigestBackfillEntry(string md5, string sha256)
{

    /// <summary>
    /// LR2 song.hash と対応する MD5 です。
    /// </summary>
    public string Md5 { get; } = md5 ?? string.Empty;

    /// <summary>
    /// 譜面ファイル全体の SHA-256 です。
    /// </summary>
    public string Sha256 { get; } = sha256 ?? string.Empty;
}

internal sealed class Lr2SongUserColumns
{
    public int? favorite { get; set; }

    public int? adddate { get; set; }

    public string tag { get; set; }
}

internal sealed class BmsLibraryDbGateway(string songDbPath, string scoreDbPath = null)
{
    internal const string SongPathNocaseIndexName = "song_idx_path_nocase";

    internal const string FolderPathNocaseIndexName = "folder_idx_path_nocase";

    internal const string MaintenancePathNocaseIndexName = "maintenance_idx_path_nocase";

    private const int ChartInfoLookupChunkSize = 500;

    private const string ChartDigestMapUpsertSql =
        "INSERT OR REPLACE INTO chart_digest_map (md5, sha256) VALUES (?, ?);";

    private const string ChartInfoUpsertSql =
        "INSERT OR REPLACE INTO chart_info ("
        + "sha256, md5, charthash, level, difficulty, difficulty_defined, mainbpm, maxbpm, minbpm, length, mode, judge, bga, exlevel, feature, notes, n, ln, s, ls, total, total_defined, density, peakdensity, enddensity, distribution, speedchange, speedchange_count, lanenotes, parser_version, updated_at"
        + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);";

    private const string ChartInfoParseFailureUpsertSql =
        "INSERT OR REPLACE INTO chart_info_parse_failure ("
        + "md5, sha256, path, parser_version, failure_kind, exception_type, message, parse_timeout_ms, updated_at"
        + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);";

    private const string ChartInfoParseFailureDeleteSql =
        "DELETE FROM chart_info_parse_failure WHERE md5 = ?;";

    private const string TempDeletedBmsPathTable = "file_scan_deleted_bms_path";

    private const string TempDeletedBmsHashTable = "file_scan_deleted_bms_hash";

    private const string TempDeletedBmsonPathTable = "file_scan_deleted_bmson_path";

    private const string TempFileScanMaintenanceUpsertTable = "file_scan_maintenance_upsert";

    private const string ChartInfoColumnList =
        "sha256, md5, charthash, level, difficulty, difficulty_defined, mainbpm, maxbpm, minbpm, length, mode, judge, bga, exlevel, feature, notes, n, ln, s, ls, total, total_defined, density, peakdensity, enddensity, distribution, speedchange, speedchange_count, lanenotes, parser_version, updated_at";

    private const string ChartInfoHydrationDisplayColumnList =
        "sha256, md5, level, difficulty, difficulty_defined, mainbpm, maxbpm, minbpm, length, mode, judge, bga, exlevel, feature, notes, n, ln, s, ls, total, total_defined, density, peakdensity, enddensity, speedchange_count, parser_version, updated_at";

    private const string ChartInfoHydrationRawSelectSql =
        "SELECT " + ChartInfoHydrationDisplayColumnList + " FROM chart_info;";

    private const string ChartInfoParseFailureHydrationRawSelectSql =
        "SELECT md5, parser_version, failure_kind, parse_timeout_ms FROM chart_info_parse_failure;";

    internal const string AppSchemaVersionName = "app_schema";

    internal const int CurrentAppSchemaVersion = 1;

    internal const int CurrentChartInfoSchemaVersion = 5;

    internal const int CurrentChartInfoParserVersion = 21;

    internal const int ChartInfoMetadataBundleFormatVersion = 1;

    internal static string SqlQuote(string value = null)
    {
        return !string.IsNullOrWhiteSpace(value) ? "'" + value.Replace("'", "''") + "'" : "''";
    }

    public string SongDbPath { get; } = songDbPath ?? throw new ArgumentNullException(nameof(songDbPath));

    public string ScoreDbPath { get; } = scoreDbPath;

    public LR2SongDBExtended OpenSongDb()
    {
        return new LR2SongDBExtended(SongDbPath);
    }

    public LR2SongDBExtended OpenSongDbReadOnly()
    {
        return new LR2SongDBExtended(SongDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, acquireProcessLock: false);
    }

    public LR2ScoreDBExtended OpenScoreDb()
    {
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            throw new InvalidOperationException("Score DB path is not configured.");
        }
        return new LR2ScoreDBExtended(ScoreDbPath);
    }

    public LR2ScoreDBExtended OpenScoreDbReadOnly()
    {
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            throw new InvalidOperationException("Score DB path is not configured.");
        }
        return new LR2ScoreDBExtended(ScoreDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, acquireProcessLock: false);
    }

    public Lr2PlayHistorySchemaCheckResult CheckLr2PlayHistorySchema(bool isLr2LinkedProfile)
    {
        return new Lr2PlayHistorySchemaService().Check(ScoreDbPath, isLr2LinkedProfile);
    }

    public Lr2PlayHistorySchemaCheckResult InstallOrRepairLr2PlayHistorySchema(bool isLr2LinkedProfile)
    {
        return new Lr2PlayHistorySchemaService().InstallOrRepair(ScoreDbPath, isLr2LinkedProfile);
    }

    public void ExecuteSongDbTransaction(Action<LR2SongDBExtended> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            action(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public void DeleteInstallRows(IEnumerable<string> installPaths)
    {
        ApplyInstallTableMutation(installPaths, []);
    }

    public void UpsertInstallRows(IEnumerable<ChartPackage> packages)
    {
        List<ChartPackage> items = [.. (packages ?? []).Where(package => package != null && !string.IsNullOrWhiteSpace(package.path))];
        if (items.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            songDb.InsertAll(items, typeof(LR2SongDBExtended.install));
        });
    }

    public void ReplaceInstallRows(IEnumerable<string> installPathsToDelete, ChartPackage packageToUpsert)
    {
        if (packageToUpsert == null || string.IsNullOrWhiteSpace(packageToUpsert.path))
        {
            throw new ArgumentNullException(nameof(packageToUpsert));
        }

        ApplyInstallTableMutation(installPathsToDelete, [packageToUpsert]);
    }

    public void ApplyInstallTableMutation(
        IEnumerable<string> installPathsToDelete,
        IEnumerable<ChartPackage> packagesToUpsert)
    {
        List<string> paths = [.. (installPathsToDelete ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        List<ChartPackage> packages = [.. (packagesToUpsert ?? [])
            .Where(package => package != null && !string.IsNullOrWhiteSpace(package.path))];
        if (paths.Count == 0 && packages.Count == 0)
        {
            return;
        }

        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (string path in paths)
            {
                songDb.Delete<LR2SongDBExtended.install>(path);
            }
            if (packages.Count > 0)
            {
                songDb.InsertAll(packages, typeof(LR2SongDBExtended.install));
            }
        });
    }

    public void UpsertSongs(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> files = [.. (bmsFiles ?? []).Where(file => file != null)];
        if (files.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            EnsureSongLookupIndexes(songDb);
            foreach (BMSFile file in files)
            {
                Lr2SongDbWriter.UpsertGeneratedSong(songDb, file);
            }
        });
    }

    internal IReadOnlyDictionary<string, Lr2SongUserColumns> CreateSongUserColumnSnapshot(IEnumerable<string> paths)
    {
        List<string> pathList = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        if (pathList.Count == 0)
        {
            return new Dictionary<string, Lr2SongUserColumns>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, Lr2SongUserColumns>(StringComparer.Ordinal);
        using LR2SongDBExtended songDb = OpenSongDb();
        foreach (string path in pathList)
        {
            Lr2SongUserColumns userColumns = ReadSongUserColumns(songDb, path);
            if (userColumns != null)
            {
                result[path] = userColumns;
            }
        }
        return result;
    }

    internal static void ApplySongUserColumns(BMSFile bmsFile, Lr2SongUserColumns userColumns)
    {
        if (bmsFile == null || userColumns == null)
        {
            return;
        }

        bmsFile.PreserveUserSongColumns(userColumns.favorite, userColumns.adddate, userColumns.tag);
    }

    public void UpdateSongLevels(IEnumerable<BMSFile> bmsFiles)
    {
        List<BMSFile> files = [.. (bmsFiles ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path) && file.level.HasValue)];
        if (files.Count == 0)
        {
            return;
        }

        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            string tableName = SQLiteTable<LR2SongDB.song>.GetTableName();
            string levelColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level);
            string pathColumn = SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path);
            foreach (BMSFile file in files)
            {
                songDb.Execute(
                    "UPDATE " + tableName
                    + " SET " + levelColumn + " = ?"
                    + " WHERE " + pathColumn + " = ?;",
                    file.level,
                    file.path);
            }
        });
    }

    internal static void PrepareFileScanDiffCommitSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        EnsureBmsonSchema(songDb);
        EnsureChartInfoSchema(songDb);
        EnsureSongLookupIndexes(songDb);
        EnsureMaintenanceSchema(songDb);
    }

    internal static FileScanDiffCommitMetrics CommitFileScanDiffChunk(
        LR2SongDBExtended songDb,
        FileScanDiffCommitChunk chunk,
        bool ensureSchema = true)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        var metrics = new FileScanDiffCommitMetrics();
        if (chunk == null || !chunk.HasItems)
        {
            return metrics;
        }

        var stopwatch = Stopwatch.StartNew();
        if (ensureSchema)
        {
            PrepareFileScanDiffCommitSchema(songDb);
        }
        stopwatch.Stop();
        metrics.SchemaMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        BulkDeleteBmsPaths(songDb, chunk.DeletedBmsPaths);
        stopwatch.Stop();
        metrics.BmsDeleteMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        foreach (BmsDateOnlyUpdate updatedDate in chunk.UpdatedBmsDates)
        {
            if (updatedDate != null && !string.IsNullOrWhiteSpace(updatedDate.Path))
            {
                Lr2SongDbWriter.UpdateMetadata(songDb, updatedDate.Path, updatedDate.Date, updatedDate.TextFlag);
            }
        }
        stopwatch.Stop();
        metrics.BmsDateUpdateMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        metrics.BmsChangedCount = Lr2SongDbWriter.UpsertGeneratedSongs(songDb, chunk.AddedBmsFiles);
        stopwatch.Stop();
        metrics.BmsUpsertMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        BulkDeleteBmsonPaths(songDb, chunk.DeletedBmsonPaths);
        stopwatch.Stop();
        metrics.BmsonDeleteMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        foreach (LR2SongDBExtended.bmson_song addedBmsonSong in chunk.UpsertBmsonSongs)
        {
            if (addedBmsonSong != null)
            {
                songDb.InsertOrReplace(addedBmsonSong, typeof(LR2SongDBExtended.bmson_song));
            }
        }
        stopwatch.Stop();
        metrics.BmsonUpsertMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        BulkUpsertMaintenanceInfos(songDb, chunk.MaintenanceInfoRows);
        stopwatch.Stop();
        metrics.MaintenanceUpsertMs = stopwatch.ElapsedMilliseconds;

        stopwatch.Restart();
        UpsertChartInfoBackfillChunk(
            songDb,
            [],
            chunk.ChartInfoRows,
            chunk.ParseFailureRows,
            chunk.ParseFailureDeleteMd5s);
        stopwatch.Stop();
        metrics.ChartInfoMs = stopwatch.ElapsedMilliseconds;
        return metrics;
    }

    public void UpsertMaintenanceInfos(IEnumerable<BMSFileMaintenanceInfo> maintenanceInfos)
    {
        List<BMSFileMaintenanceInfo> entries = [.. (maintenanceInfos ?? []).Where(info => info != null && !string.IsNullOrWhiteSpace(info.path))];
        if (entries.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureMaintenanceSchema(songDb);
            BulkUpsertMaintenanceInfos(songDb, entries);
        });
    }

    public int DeleteMaintenanceRows(IEnumerable<string> paths)
    {
        List<string> entries = [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.Ordinal)];
        if (entries.Count == 0)
        {
            return 0;
        }
        int deleted = 0;
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (string path in entries)
            {
                deleted += songDb.Delete<LR2SongDBExtended.maintenance>(path);
            }
        });
        return deleted;
    }

    public ScoreTableLoadResult LoadScoresAndPlayerId()
    {
        var result = new ScoreTableLoadResult();
        if (string.IsNullOrWhiteSpace(ScoreDbPath))
        {
            return result;
        }
        using LR2ScoreDBExtended scoreDb = OpenScoreDbReadOnly();
        result.ReadOnly = scoreDb.IsReadOnlyConnection;
        result.DbLockWaitMs = scoreDb.ProcessLockWaitMs;
        result.Scores.AddRange([.. scoreDb.Table<BMSScore>()]);
        result.LR2Id = scoreDb.Table<LR2ScoreDB.player>().ToList().FirstOrDefault()?.irid ?? 0;
        return result;
    }

    public List<ChartPackage> LoadInstallPackages()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return [.. songDb.Table<ChartPackage>()];
    }

    private static int? ParseNullableInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : null;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : 0;
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

    /// <summary>
    /// Commits relocation and removal rows as one durable catalog transaction.
    /// The caller applies live storage and package state only after this method returns.
    /// </summary>
    internal CatalogRelocationDbReceipt ReplaceAndRemoveLibraryMutationRows(
        CatalogRelocationRequest relocationRequest,
        CatalogStorageRowsRemovalRequest removalRequest,
        IEnumerable<BMSFile> addedBmsFiles = null,
        IEnumerable<LR2SongDBExtended.bmson_song> addedBmsonSongs = null)
    {
        List<BMSFile> addedBmsRows = [.. (addedBmsFiles ?? []).Where(file => file != null)];
        List<LR2SongDBExtended.bmson_song> addedBmsonRows = [.. (addedBmsonSongs ?? []).Where(song => song != null)];
        List<CatalogFolderPathReplacement> folderRows = [.. (relocationRequest?.FolderPathChanges ?? [])
            .Where(change => !string.IsNullOrWhiteSpace(change?.OldFolderPath)
                && !string.IsNullOrWhiteSpace(change.NewFolderPath))
            .GroupBy(change => NormalizeFolderRecordPath(change.OldFolderPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())];
        List<BmsSongPathReplacement> bmsRows = [.. (relocationRequest?.BmsPathReplacements ?? [])
            .Where(replacement => replacement?.Song != null
                && !string.IsNullOrWhiteSpace(replacement.Song.path)
                && !string.IsNullOrWhiteSpace(replacement.OldPath))
            .GroupBy(replacement => replacement.OldPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())];
        List<BmsonSongPathReplacement> bmsonRows = [.. (relocationRequest?.BmsonPathReplacements ?? [])
            .Where(replacement => replacement?.Song != null
                && !string.IsNullOrWhiteSpace(replacement.Song.path)
                && !string.IsNullOrWhiteSpace(replacement.OldPath))
            .GroupBy(replacement => replacement.OldPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())];
        bool hasBmsRemoval = removalRequest != null
            && (removalRequest.RemovedBmsRows.Count > 0
                || removalRequest.BmsPathCleanupKeys.Count > 0);
        bool hasBmsonRemoval = removalRequest != null
            && (removalRequest.RemovedBmsonRows.Count > 0
                || removalRequest.BmsonPathCleanupKeys.Count > 0);
        bool hasBmsUpsert = addedBmsRows.Count > 0;
        bool hasBmsonUpsert = addedBmsonRows.Count > 0;
        var result = new CatalogRelocationDbReceipt();
        if (folderRows.Count == 0
            && bmsRows.Count == 0
            && bmsonRows.Count == 0
            && !hasBmsRemoval
            && !hasBmsonRemoval
            && !hasBmsUpsert
            && !hasBmsonUpsert)
        {
            return result;
        }
        foreach (CatalogFolderPathReplacement row in folderRows)
        {
            if (!LongPathFileSystem.DirectoryExists(row.NewFolderPath))
            {
                throw new DirectoryNotFoundException(string.Format(Resources.Error_RenameDestDirNotFound, row.NewFolderPath));
            }
        }

        ExecuteSongDbTransaction(songDb =>
        {
            EnsureBmsonSchema(songDb);
            if (hasBmsRemoval || hasBmsonRemoval || hasBmsUpsert || hasBmsonUpsert)
            {
                EnsureMaintenanceSchema(songDb);
            }
            if (hasBmsUpsert)
            {
                EnsureSongLookupIndexes(songDb);
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            ReplaceFolderRecords(songDb, folderRows);
            stopwatch.Stop();
            result.FolderDbMs = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            ReplaceBmsSongPathsWithMaintenance(songDb, bmsRows);
            stopwatch.Stop();
            result.BmsPathDbMs = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            ReplaceBmsonSongPaths(songDb, bmsonRows);
            stopwatch.Stop();
            result.BmsonPathDbMs = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            DeleteBmsMutationRows(
                songDb,
                removalRequest,
                bmsRows);
            stopwatch.Stop();
            result.BmsRemovalDbMs = stopwatch.ElapsedMilliseconds;

            stopwatch.Restart();
            DeleteBmsonMutationRows(
                songDb,
                removalRequest,
                bmsonRows);
            stopwatch.Stop();
            result.BmsonRemovalDbMs = stopwatch.ElapsedMilliseconds;

            if (hasBmsUpsert)
            {
                Lr2SongDbWriter.UpsertGeneratedSongs(songDb, addedBmsRows);
            }
            if (hasBmsonUpsert)
            {
                foreach (LR2SongDBExtended.bmson_song song in addedBmsonRows)
                {
                    songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
                }
            }
        });
        return result;
    }

    private static void DeleteBmsMutationRows(
        LR2SongDBExtended songDb,
        CatalogStorageRowsRemovalRequest removalRequest,
        IReadOnlyCollection<BmsSongPathReplacement> relocations)
    {
        if (songDb == null || removalRequest == null)
        {
            return;
        }

        var removedOwners = new HashSet<BMSFile>(removalRequest.RemovedBmsRows ?? []);
        var protectedDestinationPaths = new HashSet<string>(
            (relocations ?? [])
                .Where(relocation => relocation?.Song != null
                    && !removedOwners.Contains(relocation.LiveOwner))
                .Select(relocation => relocation.Song.path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);

        var directRemovalPaths = new HashSet<string>(StringComparer.Ordinal);
        var pathCleanupKeys = new HashSet<string>(
            removalRequest.BmsPathCleanupKeys ?? [],
            StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var maintenancePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (BMSFile owner in removalRequest.RemovedBmsRows ?? [])
        {
            if (owner == null)
            {
                continue;
            }
            BmsSongPathReplacement relocation = relocations?.FirstOrDefault(
                replacement => ReferenceEquals(replacement.LiveOwner, owner));
            string path = relocation?.Song?.path ?? owner.path;
            if (!string.IsNullOrWhiteSpace(owner.hash))
            {
                hashes.Add(owner.hash);
            }
            if (protectedDestinationPaths.Contains(path))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                directRemovalPaths.Add(path);
                maintenancePaths.Add(path);
            }
        }
        List<BMSFile> rows = [];
        var selectedExactPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in directRemovalPaths)
        {
            BMSFile row = LoadBmsRowByExactPath(songDb, path);
            if (row != null && selectedExactPaths.Add(row.path))
            {
                rows.Add(row);
            }
        }
        if (pathCleanupKeys.Count > 0)
        {
            rows.AddRange(songDb.Table<BMSFile>()
                .AsEnumerable()
                .Where(row => row != null
                    && pathCleanupKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(row.path)))
                .Where(row => !protectedDestinationPaths.Contains(row.path))
                .Where(row => selectedExactPaths.Add(row.path)));
            foreach (string path in songDb.Table<BMSFileMaintenanceInfo>()
                .AsEnumerable()
                .Where(row => row != null
                    && pathCleanupKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(row.path)))
                .Where(row => !protectedDestinationPaths.Contains(row.path))
                .Select(row => row.path))
            {
                maintenancePaths.Add(path);
            }
        }
        foreach (BMSFile row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.hash))
            {
                hashes.Add(row.hash);
            }
            songDb.Delete<LR2SongDB.song>(row.path);
            maintenancePaths.Add(row.path);
        }
        foreach (string path in maintenancePaths)
        {
            songDb.Delete<LR2SongDBExtended.maintenance>(path);
        }
        DeleteChartDigestsIfOrphaned(songDb, hashes);
    }

    private static void DeleteBmsonMutationRows(
        LR2SongDBExtended songDb,
        CatalogStorageRowsRemovalRequest removalRequest,
        IReadOnlyCollection<BmsonSongPathReplacement> relocations)
    {
        if (songDb == null || removalRequest == null)
        {
            return;
        }

        var removedOwners = new HashSet<LR2SongDBExtended.bmson_song>(removalRequest.RemovedBmsonRows ?? []);
        var protectedDestinationPaths = new HashSet<string>(
            (relocations ?? [])
                .Where(relocation => relocation?.Song != null
                    && !removedOwners.Contains(relocation.LiveOwner))
                .Select(relocation => relocation.Song.path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);

        var directRemovalPaths = new HashSet<string>(StringComparer.Ordinal);
        var pathCleanupKeys = new HashSet<string>(
            removalRequest.BmsonPathCleanupKeys ?? [],
            StringComparer.OrdinalIgnoreCase);
        bool hasMaintenanceTable = TableExists(songDb, SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName());
        var maintenancePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (LR2SongDBExtended.bmson_song owner in removalRequest.RemovedBmsonRows ?? [])
        {
            if (owner == null)
            {
                continue;
            }
            BmsonSongPathReplacement relocation = relocations?.FirstOrDefault(
                replacement => ReferenceEquals(replacement.LiveOwner, owner));
            string path = relocation?.Song?.path ?? owner.path;
            if (!string.IsNullOrWhiteSpace(path)
                && !protectedDestinationPaths.Contains(path))
            {
                directRemovalPaths.Add(path);
                if (hasMaintenanceTable)
                {
                    maintenancePaths.Add(path);
                }
            }
        }
        List<LR2SongDBExtended.bmson_song> rows = [];
        var selectedExactPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in directRemovalPaths)
        {
            LR2SongDBExtended.bmson_song row = LoadBmsonRowByExactPath(songDb, path);
            if (row != null && selectedExactPaths.Add(row.path))
            {
                rows.Add(row);
            }
        }
        if (pathCleanupKeys.Count > 0)
        {
            rows.AddRange(songDb.Table<LR2SongDBExtended.bmson_song>()
                .AsEnumerable()
                .Where(row => row != null
                    && pathCleanupKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(row.path)))
                .Where(row => !protectedDestinationPaths.Contains(row.path))
                .Where(row => selectedExactPaths.Add(row.path)));
            if (hasMaintenanceTable)
            {
                foreach (string path in songDb.Table<BMSFileMaintenanceInfo>()
                    .AsEnumerable()
                    .Where(row => row != null
                        && pathCleanupKeys.Contains(OwnedChartCollectionState.CreateOwnedPathKey(row.path)))
                    .Where(row => !protectedDestinationPaths.Contains(row.path))
                    .Select(row => row.path))
                {
                    maintenancePaths.Add(path);
                }
            }
        }
        foreach (LR2SongDBExtended.bmson_song row in rows)
        {
            songDb.Delete<LR2SongDBExtended.bmson_song>(row.path);
            if (hasMaintenanceTable)
            {
                maintenancePaths.Add(row.path);
            }
        }
        if (hasMaintenanceTable)
        {
            foreach (string path in maintenancePaths)
            {
                songDb.Delete<LR2SongDBExtended.maintenance>(path);
            }
        }
    }

    private static BMSFile LoadBmsRowByExactPath(LR2SongDBExtended songDb, string path)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return songDb.Query<BMSFile>(
            "SELECT * FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " = ? LIMIT 1;",
            path).FirstOrDefault();
    }

    private static LR2SongDBExtended.bmson_song LoadBmsonRowByExactPath(
        LR2SongDBExtended songDb,
        string path)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return songDb.Query<LR2SongDBExtended.bmson_song>(
            "SELECT * FROM " + SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.path)
            + " = ? LIMIT 1;",
            path).FirstOrDefault();
    }

    private static Lr2SongUserColumns ReadSongUserColumns(LR2SongDBExtended songDb, string oldPath)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(oldPath))
        {
            return null;
        }

        return songDb.Query<Lr2SongUserColumns>(
            "SELECT "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.favorite) + " AS favorite, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.adddate) + " AS adddate, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.tag) + " AS tag"
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.path) + " = ? LIMIT 1;",
            oldPath).FirstOrDefault();
    }

    internal static void ApplySongUserColumns(LR2SongDBExtended songDb, string path, Lr2SongUserColumns userColumns)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(path) || userColumns == null)
        {
            return;
        }

        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.favorite) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.adddate) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.tag) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.path) + " = ?;",
            userColumns.favorite,
            userColumns.adddate,
            userColumns.tag,
            path);
    }

    public void EnsureBmsonSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureBmsonSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public void EnsureMaintenanceSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureMaintenanceSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public void EnsureAppOwnedSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        ExecuteAppOwnedSchemaTransaction(songDb, EnsureAppOwnedSchema);
    }

    public void EnsureLibraryStartupSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureSongLookupIndexes(songDb);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.CreateTable<LR2SongDBExtended.install>();
        EnsureMaintenanceSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.ir_score>();
        EnsureIrDataSchema(songDb);
        EnsureChartInfoSchema(songDb);
    }

    /// <summary>
    /// chart_info テーブルと関連 index を作成または修復します。
    /// </summary>
    public void EnsureChartInfoSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureChartInfoSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public void RepairAppOwnedSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        ExecuteAppOwnedSchemaTransaction(songDb, RepairAppOwnedSchemaOnConnection);
    }

    private static void ExecuteAppOwnedSchemaTransaction(
        LR2SongDBExtended songDb,
        Action<LR2SongDBExtended> participant)
    {
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            participant(songDb);
            songDb.Commit();
        }
        catch
        {
            try
            {
                songDb.RollbackTo(savepoint);
            }
            catch
            {
                // Keep the schema failure authoritative when rollback itself fails.
            }
            throw;
        }
    }

    /// <summary>
    /// chart_info schema と app-owned schema version が現行かどうかを返します。
    /// </summary>
    /// <returns>現行 schema であれば true。</returns>
    public bool IsChartInfoSchemaCurrent()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return IsChartInfoSchemaCurrent(songDb);
    }

    /// <summary>
    /// app-owned schema version を現行として記録します。
    /// </summary>
    public void MarkChartInfoSchemaCurrent()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            SetCurrentAppSchemaVersion(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public Dictionary<string, string> LoadChartDigestMap()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        return LoadChartDigestMap(songDb);
    }

    public Dictionary<string, string> LoadChartDigestMapByMd5(IEnumerable<string> md5s)
    {
        List<string> keys = NormalizeChartInfoLookupKeys(md5s);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }
        using LR2SongDBExtended songDb = OpenSongDb();
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return result;
        }
        for (int offset = 0; offset < keys.Count; offset += ChartInfoLookupChunkSize)
        {
            List<string> chunk = [.. keys.Skip(offset).Take(ChartInfoLookupChunkSize)];
            if (chunk.Count == 0)
            {
                continue;
            }
            string placeholders = string.Join(", ", chunk.Select(_ => "?"));
            string sql = "SELECT md5, sha256 FROM " + tableName
                + " WHERE md5 IN (" + placeholders + ")"
                + " AND md5 IS NOT NULL AND TRIM(md5) <> ''"
                + " AND sha256 IS NOT NULL AND TRIM(sha256) <> '';";
            foreach (ChartDigestQueryRow row in songDb.Query<ChartDigestQueryRow>(sql, [.. chunk.Cast<object>()]))
            {
                if (row != null && !string.IsNullOrWhiteSpace(row.md5) && !string.IsNullOrWhiteSpace(row.sha256))
                {
                    result[row.md5] = row.sha256;
                }
            }
        }
        return result;
    }

    internal Dictionary<string, string> LoadChartDigestMap(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartDigestQueryRow item in songDb.Query<ChartDigestQueryRow>("SELECT md5, sha256 FROM " + tableName + " WHERE md5 IS NOT NULL AND TRIM(md5) <> '' AND sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.md5) && !string.IsNullOrWhiteSpace(item.sha256))
            {
                dictionary[item.md5] = item.sha256;
            }
        }
        return dictionary;
    }

    /// <summary>
    /// chart_info を sha256 keyed dictionary として読み込みます。
    /// </summary>
    /// <returns>sha256 をキーにした譜面解析メタデータ。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfoMap()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        var dictionary = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info item in songDb.Table<LR2SongDBExtended.chart_info>())
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.sha256))
            {
                dictionary[item.sha256] = item;
            }
        }
        return dictionary;
    }

    /// <summary>
    /// 現行 parser version の chart_info を read-only connection から sha256 keyed dictionary として読み込みます。
    /// </summary>
    /// <returns>schema が current でない、または read-only load に失敗した場合は null。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> TryLoadCurrentChartInfoMapReadOnly()
    {
        try
        {
            using LR2SongDBExtended songDb = OpenSongDbReadOnly();
            if (!IsChartInfoSchemaCurrent(songDb))
            {
                return null;
            }

            var dictionary = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
            string sql = "SELECT " + ChartInfoColumnList
                + " FROM chart_info WHERE sha256 IS NOT NULL AND TRIM(sha256) <> '' AND parser_version >= "
                + CurrentChartInfoParserVersion
                + ";";
            foreach (LR2SongDBExtended.chart_info item in songDb.Query<LR2SongDBExtended.chart_info>(sql))
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.sha256))
                {
                    dictionary[item.sha256] = item;
                }
            }
            return dictionary;
        }
        catch (SQLiteException)
        {
            return null;
        }
    }

    public ChartInfoHydrationLoadResult LoadChartInfoHydrationData(TimeSpan parseTimeout)
    {
        var result = new ChartInfoHydrationLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        if (UseRawChartInfoHydrationLoader())
        {
            LoadChartInfoHydrationDataRaw(songDb, parseTimeout, result);
            return result;
        }

        result.MaterializeMode = "sqlite_net";
        var stopwatch = Stopwatch.StartNew();
        foreach (LR2SongDBExtended.chart_info item in songDb.Table<LR2SongDBExtended.chart_info>())
        {
            result.ChartInfoRows++;
            if (item != null && !string.IsNullOrWhiteSpace(item.sha256))
            {
                result.ChartInfoBySha256[item.sha256] = item;
                if (item.parser_version >= CurrentChartInfoParserVersion)
                {
                    result.CurrentChartInfoSha256s.Add(item.sha256);
                }
            }
        }
        foreach (LR2SongDBExtended.chart_info_parse_failure row in songDb.Table<LR2SongDBExtended.chart_info_parse_failure>())
        {
            result.ParseFailureRows++;
            if (IsCurrentChartInfoParseFailure(row, parseTimeout))
            {
                result.CurrentParseFailureMd5s.Add(row.md5);
            }
        }
        stopwatch.Stop();
        result.DbReadMs = stopwatch.ElapsedMilliseconds;
        result.MaterializeMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    private static bool UseRawChartInfoHydrationLoader()
    {
        string mode = Environment.GetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE");
        return !string.Equals(mode, "sqlite_net", StringComparison.OrdinalIgnoreCase);
    }

    private static void LoadChartInfoHydrationDataRaw(
        LR2SongDBExtended songDb,
        TimeSpan parseTimeout,
        ChartInfoHydrationLoadResult result)
    {
        result.MaterializeMode = "raw_string_display";
        long objectTicks = 0L;
        long stopwatchFrequency = Stopwatch.Frequency;
        var stopwatch = Stopwatch.StartNew();
        var chartInfoCommand = (LR2SongDBExtended.SQLiteCommandExtended)songDb.CreateCommand(ChartInfoHydrationRawSelectSql);
        int rawRows = chartInfoCommand.ForEachRawValueAsString(delegate (string[] values)
        {
            long objectStart = Stopwatch.GetTimestamp();
            LR2SongDBExtended.chart_info item = CreateChartInfoHydrationDisplayRow(values);
            result.ChartInfoRows++;
            if (item != null && !string.IsNullOrWhiteSpace(item.sha256))
            {
                result.ChartInfoBySha256[item.sha256] = item;
                if (item.parser_version >= CurrentChartInfoParserVersion)
                {
                    result.CurrentChartInfoSha256s.Add(item.sha256);
                }
            }
            objectTicks += Stopwatch.GetTimestamp() - objectStart;
        });
        var parseFailureCommand = (LR2SongDBExtended.SQLiteCommandExtended)songDb.CreateCommand(ChartInfoParseFailureHydrationRawSelectSql);
        rawRows += parseFailureCommand.ForEachRawValueAsString(delegate (string[] values)
        {
            long objectStart = Stopwatch.GetTimestamp();
            result.ParseFailureRows++;
            string md5 = GetRawValue(values, 0);
            int parserVersion = ParseInt(GetRawValue(values, 1));
            string failureKind = GetRawValue(values, 2);
            int? parseTimeoutMs = ParseNullableInt(GetRawValue(values, 3));
            if (!string.IsNullOrWhiteSpace(md5)
                && IsCurrentChartInfoParseFailure(parserVersion, failureKind, parseTimeoutMs, parseTimeout))
            {
                result.CurrentParseFailureMd5s.Add(md5);
            }
            objectTicks += Stopwatch.GetTimestamp() - objectStart;
        });
        stopwatch.Stop();
        long totalMs = stopwatch.ElapsedMilliseconds;
        result.RawRows = rawRows;
        result.RawObjectMs = stopwatchFrequency > 0L ? objectTicks * 1000L / stopwatchFrequency : 0L;
        result.RawReadMs = Math.Max(0L, totalMs - result.RawObjectMs);
        result.DbReadMs = totalMs;
        result.MaterializeMs = totalMs;
    }

    private static LR2SongDBExtended.chart_info CreateChartInfoHydrationDisplayRow(string[] values)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = GetRawValue(values, 0),
            md5 = GetRawValue(values, 1),
            level = ParseNullableInt(GetRawValue(values, 2)),
            difficulty = ParseNullableInt(GetRawValue(values, 3)),
            difficulty_defined = ParseBoolean(GetRawValue(values, 4)),
            mainbpm = ParseNullableDouble(GetRawValue(values, 5)),
            maxbpm = ParseNullableDouble(GetRawValue(values, 6)),
            minbpm = ParseNullableDouble(GetRawValue(values, 7)),
            length = ParseNullableInt(GetRawValue(values, 8)),
            mode = ParseNullableInt(GetRawValue(values, 9)),
            judge = ParseNullableInt(GetRawValue(values, 10)),
            bga = ParseNullableInt(GetRawValue(values, 11)),
            exlevel = ParseNullableInt(GetRawValue(values, 12)),
            feature = ParseInt(GetRawValue(values, 13)),
            notes = ParseInt(GetRawValue(values, 14)),
            n = ParseInt(GetRawValue(values, 15)),
            ln = ParseInt(GetRawValue(values, 16)),
            s = ParseInt(GetRawValue(values, 17)),
            ls = ParseInt(GetRawValue(values, 18)),
            total = ParseNullableDouble(GetRawValue(values, 19)),
            total_defined = ParseBoolean(GetRawValue(values, 20)),
            density = ParseNullableDouble(GetRawValue(values, 21)),
            peakdensity = ParseNullableDouble(GetRawValue(values, 22)),
            enddensity = ParseNullableDouble(GetRawValue(values, 23)),
            speedchange_count = ParseInt(GetRawValue(values, 24)),
            parser_version = ParseInt(GetRawValue(values, 25)),
            updated_at = ParseNullableDateTime(GetRawValue(values, 26)) ?? default
        };
    }

    private static string GetRawValue(string[] values, int index)
    {
        return values != null && index >= 0 && index < values.Length ? values[index] : null;
    }

    /// <summary>
    /// 指定された SHA-256 だけに対応する chart_info を読み込みます。
    /// playlist の未所持行解決や inline chart_info で全件読み込みを避けるために使用します。
    /// </summary>
    /// <param name="sha256s">検索対象 SHA-256。</param>
    /// <returns>SHA-256 をキーにした chart_info。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosBySha256(IEnumerable<string> sha256s)
    {
        List<string> keys = NormalizeChartInfoLookupKeys(sha256s);
        var result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }
        foreach (LR2SongDBExtended.chart_info row in LoadChartInfoRowsByColumn("sha256", keys, orderBySha256: false))
        {
            if (row != null && !string.IsNullOrWhiteSpace(row.sha256))
            {
                result[row.sha256] = row;
            }
        }
        return result;
    }

    public bool IsChartInfoMetadataBundleImportRecorded(string bundleSha256)
    {
        if (string.IsNullOrWhiteSpace(bundleSha256))
        {
            return false;
        }

        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureChartInfoSchema(songDb);
            bool imported = IsChartInfoMetadataBundleImported(songDb, BuildChartInfoMetadataBundleImportKey(bundleSha256));
            songDb.Commit();
            return imported;
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    /// <summary>
    /// 指定された MD5 だけに対応する chart_info を読み込みます。
    /// 同一 MD5 に複数 SHA-256 が存在する場合は SHA-256 昇順で最初の行を採用します。
    /// </summary>
    /// <param name="md5s">検索対象 MD5。</param>
    /// <returns>MD5 をキーにした chart_info。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info> LoadChartInfosByMd5(IEnumerable<string> md5s)
    {
        List<string> keys = NormalizeChartInfoLookupKeys(md5s);
        var result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        if (keys.Count == 0)
        {
            return result;
        }
        foreach (LR2SongDBExtended.chart_info row in LoadChartInfoRowsByColumn("md5", keys, orderBySha256: true))
        {
            if (row != null && !string.IsNullOrWhiteSpace(row.md5) && !result.ContainsKey(row.md5))
            {
                result[row.md5] = row;
            }
        }
        return result;
    }

    /// <summary>
    /// 現行 parser / timeout 条件で有効な chart_info 解析失敗記録を MD5 keyed dictionary として読み込みます。
    /// </summary>
    /// <param name="parseTimeout">今回の解析 timeout。</param>
    /// <returns>MD5 をキーにした解析失敗記録。</returns>
    public Dictionary<string, LR2SongDBExtended.chart_info_parse_failure> LoadCurrentChartInfoParseFailureMap(TimeSpan parseTimeout)
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        var result = new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info_parse_failure row in songDb.Table<LR2SongDBExtended.chart_info_parse_failure>())
        {
            if (IsCurrentChartInfoParseFailure(row, parseTimeout))
            {
                result[row.md5] = row;
            }
        }
        return result;
    }

    public ChartInfoBackfillCandidateSummary GetChartInfoBackfillCandidateSummary(TimeSpan parseTimeout)
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureBmsonSchema(songDb);
        EnsureChartInfoSchema(songDb);

        var summary = new ChartInfoBackfillCandidateSummary();
        string songTable = SQLiteTable<LR2SongDB.song>.GetTableName();
        string bmsonTable = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        Dictionary<string, string> digestByMd5 = LoadChartDigestMapForCandidateSummary(songDb);
        var anyChartInfoSha256 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentChartInfoSha256 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartInfoSummaryRow row in songDb.Query<ChartInfoSummaryRow>("SELECT sha256, parser_version FROM chart_info WHERE sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            string sha256 = NormalizeLookupKey(row.sha256);
            if (string.IsNullOrWhiteSpace(sha256))
            {
                continue;
            }
            anyChartInfoSha256.Add(sha256);
            if (row.parser_version >= CurrentChartInfoParserVersion)
            {
                currentChartInfoSha256.Add(sha256);
            }
        }
        var currentParseFailureMd5 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartInfoParseFailureSummaryRow row in songDb.Query<ChartInfoParseFailureSummaryRow>("SELECT md5, parser_version, failure_kind, parse_timeout_ms FROM chart_info_parse_failure WHERE md5 IS NOT NULL AND TRIM(md5) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            if (IsCurrentChartInfoParseFailure(row.parser_version, row.failure_kind, row.parse_timeout_ms, parseTimeout))
            {
                currentParseFailureMd5.Add(md5);
            }
        }

        foreach (ChartInfoOwnerMd5Row row in songDb.Query<ChartInfoOwnerMd5Row>("SELECT hash AS md5 FROM " + songTable + " WHERE hash IS NOT NULL AND TRIM(hash) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            if (string.IsNullOrWhiteSpace(md5))
            {
                continue;
            }
            summary.BmsOwnerCount++;
            digestByMd5.TryGetValue(md5, out string sha256);
            ClassifyChartInfoBackfillCandidate(summary, md5, sha256, currentChartInfoSha256, anyChartInfoSha256, currentParseFailureMd5, missingDigestWhenShaMissing: true);
        }

        foreach (BmsonChartInfoOwnerRow row in songDb.Query<BmsonChartInfoOwnerRow>("SELECT md5, sha256 FROM " + bmsonTable + " WHERE path IS NOT NULL AND TRIM(path) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            string sha256 = NormalizeLookupKey(row.sha256);
            summary.BmsonOwnerCount++;
            ClassifyChartInfoBackfillCandidate(summary, md5, sha256, currentChartInfoSha256, anyChartInfoSha256, currentParseFailureMd5, missingDigestWhenShaMissing: true);
        }
        return summary;
    }

    public void UpsertChartInfoParseFailures(IEnumerable<LR2SongDBExtended.chart_info_parse_failure> rows)
    {
        List<LR2SongDBExtended.chart_info_parse_failure> sourceRows = NormalizeChartInfoParseFailureRows(rows);
        if (sourceRows.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureChartInfoSchema(songDb);
            foreach (LR2SongDBExtended.chart_info_parse_failure row in sourceRows)
            {
                ExecuteChartInfoParseFailureUpsert(songDb, row);
            }
        });
    }

    public void DeleteChartInfoParseFailuresByMd5(IEnumerable<string> md5s)
    {
        List<string> sourceMd5s = NormalizeChartInfoLookupKeys(md5s);
        if (sourceMd5s.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureChartInfoSchema(songDb);
            foreach (string md5 in sourceMd5s)
            {
                songDb.Execute(ChartInfoParseFailureDeleteSql, md5);
            }
        });
    }

    public List<LR2SongDBExtended.bmson_song> LoadBmsonSongs()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()))
        {
            return [];
        }
        return [.. songDb.Table<LR2SongDBExtended.bmson_song>()];
    }

    /// <summary>
    /// 解析済み chart_info 行を保存します。
    /// </summary>
    /// <param name="rows">保存する譜面解析メタデータ。</param>
    public void UpsertChartInfos(IEnumerable<LR2SongDBExtended.chart_info> rows)
    {
        List<LR2SongDBExtended.chart_info> sourceRows = [.. (rows ?? []).Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))];
        if (sourceRows.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureChartInfoSchema(songDb);
            foreach (LR2SongDBExtended.chart_info row in sourceRows)
            {
                ExecuteChartInfoUpsert(songDb, row);
            }
        });
    }

    /// <summary>
    /// chart_info backfill で使う app 独自 table を一度に準備します。
    /// chunk commit ごとに schema repair を繰り返さないため、backfill 開始時に呼びます。
    /// </summary>
    public void EnsureChartInfoBackfillSchema()
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        string savepoint = songDb.SaveTransactionPoint();
        try
        {
            EnsureBmsonSchema(songDb);
            EnsureChartInfoSchema(songDb);
            songDb.Commit();
        }
        catch (Exception)
        {
            songDb.RollbackTo(savepoint);
            throw;
        }
    }

    public ChartInfoMetadataBundleImportResult ImportChartInfoMetadataBundle(string bundleDbPath, string bundleSha256)
    {
        if (string.IsNullOrWhiteSpace(bundleDbPath))
        {
            throw new ArgumentNullException(nameof(bundleDbPath));
        }
        if (!File.Exists(bundleDbPath))
        {
            throw new FileNotFoundException("chart_info metadata bundle was not found.", bundleDbPath);
        }
        if (string.IsNullOrWhiteSpace(bundleSha256))
        {
            throw new ArgumentNullException(nameof(bundleSha256));
        }
        bundleSha256 = bundleSha256.Trim().ToLowerInvariant();
        var stopwatch = Stopwatch.StartNew();
        var result = new ChartInfoMetadataBundleImportResult
        {
            BundleSha256 = bundleSha256
        };
        using LR2SongDBExtended songDb = OpenSongDb();
        bool attached = false;
        string savepoint = null;
        Exception importException = null;
        try
        {
            EnsureChartInfoSchema(songDb);
            EnsureBmsonSchema(songDb);
            songDb.Execute("ATTACH DATABASE ? AS bundle;", bundleDbPath);
            attached = true;
            savepoint = songDb.SaveTransactionPoint();
            ChartInfoMetadataBundleManifest manifest = LoadChartInfoMetadataBundleManifest(songDb);
            result.BundleId = manifest.bundle_id;
            result.SourceChartInfoCount = Math.Max(0, manifest.chart_info_count);
            result.SourceDigestCount = Math.Max(0, manifest.chart_digest_count);

            string importKey = BuildChartInfoMetadataBundleImportKey(bundleSha256);
            if (IsChartInfoMetadataBundleImported(songDb, importKey))
            {
                result.Skipped = true;
                result.SkipReason = "already_imported";
                songDb.Commit();
                stopwatch.Stop();
                result.ElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            PrepareChartInfoMetadataImportTempTables(songDb);
            result.ImportedChartInfoCount = songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM temp.chart_info_metadata_import_info_to_import;");
            result.ImportedDigestCount = songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM temp.chart_info_metadata_import_digest_to_import;");
            result.FailureClearedCount = songDb.ExecuteScalar<int>("SELECT COUNT(1) FROM temp.chart_info_metadata_import_failure_to_clear;");

            songDb.Execute(
                "INSERT OR REPLACE INTO main.chart_info ("
                + ChartInfoColumnList
                + ") SELECT "
                + ChartInfoColumnList
                + " FROM temp.chart_info_metadata_import_info_to_import;");
            songDb.Execute(
                "INSERT OR IGNORE INTO main.chart_digest_map (md5, sha256) "
                + "SELECT md5, sha256 FROM temp.chart_info_metadata_import_digest_to_import;");
            songDb.Execute(
                "DELETE FROM main.chart_info_parse_failure WHERE md5 IN (SELECT md5 FROM temp.chart_info_metadata_import_failure_to_clear);");
            songDb.InsertOrReplace(new LR2SongDBExtended.chart_info_import_history
            {
                import_key = importKey,
                bundle_id = result.BundleId,
                bundle_sha256 = bundleSha256,
                parser_version = CurrentChartInfoParserVersion,
                chart_info_imported_count = result.ImportedChartInfoCount,
                chart_digest_imported_count = result.ImportedDigestCount,
                failure_cleared_count = result.FailureClearedCount,
                imported_at = DateTime.UtcNow
            }, typeof(LR2SongDBExtended.chart_info_import_history));
            songDb.Commit();
        }
        catch (Exception ex)
        {
            importException = ex;
            if (savepoint != null)
            {
                try
                {
                    songDb.RollbackTo(savepoint);
                }
                catch
                {
                }
            }
            throw;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    songDb.Execute("DETACH DATABASE bundle;");
                }
                catch
                {
                    if (importException == null)
                    {
                        throw;
                    }
                }
            }
            stopwatch.Stop();
            result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        }
        return result;
    }

    /// <summary>
    /// chart_info backfill の1 chunk 分を短い transaction で保存します。
    /// digest と chart_info をまとめて保存し、途中終了時は次回 backfill が未保存分だけを再開します。
    /// </summary>
    /// <param name="digestEntries">補完した MD5/SHA-256 対応。</param>
    /// <param name="rows">保存する譜面解析メタデータ。</param>
    /// <param name="parseFailureRows">保存する解析失敗記録。</param>
    /// <param name="parseFailureDeleteMd5s">削除する解析失敗記録の MD5。</param>
    public void UpsertChartInfoBackfillChunk(
        IEnumerable<ChartDigestBackfillEntry> digestEntries,
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        IEnumerable<LR2SongDBExtended.chart_info_parse_failure> parseFailureRows = null,
        IEnumerable<string> parseFailureDeleteMd5s = null)
    {
        List<ChartDigestBackfillEntry> sourceDigestEntries = [.. (digestEntries ?? []).Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Md5) && !string.IsNullOrWhiteSpace(entry.Sha256))];
        List<LR2SongDBExtended.chart_info> sourceRows = [.. (rows ?? []).Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))];
        List<LR2SongDBExtended.chart_info_parse_failure> sourceParseFailureRows = NormalizeChartInfoParseFailureRows(parseFailureRows);
        List<string> sourceParseFailureDeleteMd5s = NormalizeChartInfoLookupKeys(parseFailureDeleteMd5s);
        if (sourceDigestEntries.Count == 0 && sourceRows.Count == 0 && sourceParseFailureRows.Count == 0 && sourceParseFailureDeleteMd5s.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            UpsertChartInfoBackfillChunk(songDb, sourceDigestEntries, sourceRows, sourceParseFailureRows, sourceParseFailureDeleteMd5s);
        });
    }

    internal static void UpsertChartInfoBackfillChunk(
        LR2SongDBExtended songDb,
        IEnumerable<ChartDigestBackfillEntry> digestEntries,
        IEnumerable<LR2SongDBExtended.chart_info> rows,
        IEnumerable<LR2SongDBExtended.chart_info_parse_failure> parseFailureRows = null,
        IEnumerable<string> parseFailureDeleteMd5s = null)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        List<ChartDigestBackfillEntry> sourceDigestEntries = [.. (digestEntries ?? []).Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Md5) && !string.IsNullOrWhiteSpace(entry.Sha256))];
        List<LR2SongDBExtended.chart_info> sourceRows = [.. (rows ?? []).Where(row => row != null && !string.IsNullOrWhiteSpace(row.sha256))];
        List<LR2SongDBExtended.chart_info_parse_failure> sourceParseFailureRows = NormalizeChartInfoParseFailureRows(parseFailureRows);
        List<string> sourceParseFailureDeleteMd5s = NormalizeChartInfoLookupKeys(parseFailureDeleteMd5s);
        if (sourceDigestEntries.Count == 0 && sourceRows.Count == 0 && sourceParseFailureRows.Count == 0 && sourceParseFailureDeleteMd5s.Count == 0)
        {
            return;
        }
        EnsureChartInfoSchema(songDb);
        foreach (ChartDigestBackfillEntry entry in sourceDigestEntries)
        {
            songDb.Execute(ChartDigestMapUpsertSql, entry.Md5, entry.Sha256);
        }
        foreach (LR2SongDBExtended.chart_info row in sourceRows)
        {
            ExecuteChartInfoUpsert(songDb, row);
        }
        foreach (string md5 in sourceParseFailureDeleteMd5s)
        {
            songDb.Execute(ChartInfoParseFailureDeleteSql, md5);
        }
        foreach (LR2SongDBExtended.chart_info_parse_failure row in sourceParseFailureRows)
        {
            ExecuteChartInfoParseFailureUpsert(songDb, row);
        }
    }

    private static void ExecuteChartInfoUpsert(LR2SongDBExtended songDb, LR2SongDBExtended.chart_info row)
    {
        songDb.Execute(
            ChartInfoUpsertSql,
            row.sha256,
            row.md5,
            row.charthash,
            row.level,
            row.difficulty,
            row.difficulty_defined ? 1 : 0,
            row.mainbpm,
            row.maxbpm,
            row.minbpm,
            row.length,
            row.mode,
            row.judge,
            row.bga,
            row.exlevel,
            row.feature,
            row.notes,
            row.n,
            row.ln,
            row.s,
            row.ls,
            row.total,
            row.total_defined ? 1 : 0,
            row.density,
            row.peakdensity,
            row.enddensity,
            row.distribution,
            row.speedchange,
            row.speedchange_count,
            row.lanenotes,
            row.parser_version,
            row.updated_at);
    }

    private static void ExecuteChartInfoParseFailureUpsert(LR2SongDBExtended songDb, LR2SongDBExtended.chart_info_parse_failure row)
    {
        songDb.Execute(
            ChartInfoParseFailureUpsertSql,
            row.md5,
            row.sha256,
            row.path,
            row.parser_version,
            row.failure_kind,
            row.exception_type,
            row.message,
            row.parse_timeout_ms,
            row.updated_at);
    }

    public void UpsertChartDigests(IEnumerable<BMSFile> files)
    {
        List<BMSFile> sourceFiles = [.. (files ?? []).Where(file => file != null && !string.IsNullOrWhiteSpace(file.hash) && !string.IsNullOrWhiteSpace(file.sha256))];
        if (sourceFiles.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            foreach (BMSFile file in sourceFiles)
            {
                UpsertChartDigest(songDb, file);
            }
        });
    }

    public void UpsertBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> songs)
    {
        List<LR2SongDBExtended.bmson_song> sourceSongs = [.. (songs ?? []).Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        if (sourceSongs.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            EnsureBmsonSchema(songDb);
            foreach (LR2SongDBExtended.bmson_song song in sourceSongs)
            {
                songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
            }
        });
    }

    private static string NormalizeFolderRecordPath(string path)
    {
        return path?.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    private static void ReplaceBmsSongPathsWithMaintenance(LR2SongDBExtended songDb, IReadOnlyCollection<BmsSongPathReplacement> rows)
    {
        if (songDb == null || rows == null || rows.Count == 0)
        {
            return;
        }

        var userColumnsByOldPath = new Dictionary<string, Lr2SongUserColumns>(StringComparer.Ordinal);
        foreach (BmsSongPathReplacement replacement in rows)
        {
            Lr2SongUserColumns userColumns = ReadSongUserColumns(songDb, replacement.OldPath);
            if (userColumns != null)
            {
                userColumnsByOldPath[replacement.OldPath] = userColumns;
            }
            songDb.Delete<LR2SongDB.song>(replacement.OldPath);
            songDb.Delete<LR2SongDBExtended.maintenance>(replacement.OldPath);
            songDb.Delete<LR2SongDBExtended.maintenance>(replacement.Song.path);
        }

        foreach (BmsSongPathReplacement replacement in rows)
        {
            if (replacement.MaintenanceInfo != null)
            {
                songDb.InsertOrReplace(replacement.MaintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
        }

        Lr2SongDbWriter.UpsertGeneratedSongs(songDb, [.. rows.Select(replacement => replacement.Song)]);
        foreach (BmsSongPathReplacement replacement in rows)
        {
            if (userColumnsByOldPath.TryGetValue(replacement.OldPath, out Lr2SongUserColumns userColumns))
            {
                ApplySongUserColumns(songDb, replacement.Song.path, userColumns);
            }
        }
    }

    private static void ReplaceBmsonSongPaths(LR2SongDBExtended songDb, IReadOnlyCollection<BmsonSongPathReplacement> rows)
    {
        if (songDb == null || rows == null || rows.Count == 0)
        {
            return;
        }

        bool hasMaintenanceTable = TableExists(songDb, SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName());
        foreach (BmsonSongPathReplacement replacement in rows)
        {
            songDb.Delete<LR2SongDBExtended.bmson_song>(replacement.OldPath);
            if (hasMaintenanceTable)
            {
                songDb.Delete<LR2SongDBExtended.maintenance>(replacement.OldPath);
                songDb.Delete<LR2SongDBExtended.maintenance>(replacement.Song.path);
            }
        }

        foreach (LR2SongDBExtended.bmson_song song in rows.Select(replacement => replacement.Song))
        {
            songDb.InsertOrReplace(song, typeof(LR2SongDBExtended.bmson_song));
            if (hasMaintenanceTable && song.MaintenanceInfo != null)
            {
                song.MaintenanceInfo.NormalizeForBmson(song.path, song.md5);
                songDb.InsertOrReplace(song.MaintenanceInfo, typeof(LR2SongDBExtended.maintenance));
            }
        }
    }

    private static int ReplaceFolderRecords(LR2SongDBExtended songDb, IReadOnlyCollection<CatalogFolderPathReplacement> rows)
    {
        if (songDb == null || rows == null || rows.Count == 0)
        {
            return 0;
        }

        int replaced = 0;
        Dictionary<string, CatalogFolderPathReplacement> changesByOldPath = rows.ToDictionary(
            change => NormalizeFolderRecordPath(change.OldFolderPath),
            change => change,
            StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDB.folder folder in songDb.Table<LR2SongDB.folder>().ToList())
        {
            if (folder == null || !changesByOldPath.TryGetValue(folder.path, out CatalogFolderPathReplacement change))
            {
                continue;
            }
            replaced++;
            songDb.Delete<LR2SongDB.folder>(folder.path);
            folder.title = Path.GetFileName(change.NewFolderPath);
            folder.path = NormalizeFolderRecordPath(change.NewFolderPath);
            if (folder.parent != Lr2SongFolderParentNormalizer.RootParentHash)
            {
                string directoryName = Path.GetDirectoryName(change.NewFolderPath.TrimEnd(Path.DirectorySeparatorChar));
                folder.parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(directoryName);
            }
            songDb.InsertOrReplace(folder, typeof(LR2SongDB.folder));
        }
        return replaced;
    }

    public List<LR2IRData> LoadIrData(int lr2Id)
    {
        return LoadIrDataWithMetrics(lr2Id).Rows;
    }

    public List<LR2IRScore> LoadIrScoreRows()
    {
        return LoadIrScoreRowsWithMetrics().Rows;
    }

    public IrScoreRowsLoadResult LoadIrScoreRowsWithMetrics()
    {
        var result = new IrScoreRowsLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        result.Rows.AddRange(songDb.Query<LR2IRScore>("SELECT * FROM " + SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName() + ";"));
        return result;
    }

    public LR2SongDBExtended.ir_score_refresh_metadata LoadIrScoreRefreshMetadata(int lr2Id)
    {
        return LoadIrScoreRefreshMetadata(lr2Id, out _);
    }

    public LR2SongDBExtended.ir_score_refresh_metadata LoadIrScoreRefreshMetadata(int lr2Id, out long dbLockWaitMs)
    {
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        dbLockWaitMs = songDb.ProcessLockWaitMs;
        return songDb.Table<LR2SongDBExtended.ir_score_refresh_metadata>().FirstOrDefault(row => row.lr2id == lr2Id);
    }

    public void UpsertIrScoreRefreshMetadata(int lr2Id, string scoreDigestSha256)
    {
        using LR2SongDBExtended songDb = OpenSongDb();
        songDb.CreateTable<LR2SongDBExtended.ir_score_refresh_metadata>();
        songDb.InsertOrReplace(new LR2SongDBExtended.ir_score_refresh_metadata
        {
            lr2id = lr2Id,
            score_digest_sha256 = scoreDigestSha256 ?? string.Empty,
            updated_at = DateTime.UtcNow
        }, typeof(LR2SongDBExtended.ir_score_refresh_metadata));
    }

    public IrDataLoadResult LoadIrDataWithMetrics(int lr2Id)
    {
        var result = new IrDataLoadResult();
        using LR2SongDBExtended songDb = OpenSongDbReadOnly();
        result.ReadOnly = songDb.IsReadOnlyConnection;
        result.DbLockWaitMs = songDb.ProcessLockWaitMs;
        string tableName = SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName();
        string lr2IdColumn = SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(row => row.lr2id);
        var stopwatch = Stopwatch.StartNew();
        result.Rows.AddRange(songDb.Query<LR2IRData>("SELECT * FROM " + tableName + " WHERE " + lr2IdColumn + " = ?;", lr2Id));
        stopwatch.Stop();
        result.DbReadMs = stopwatch.ElapsedMilliseconds;
        result.MaterializeMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    public void UpsertIrData(IEnumerable<LR2IRData> irData)
    {
        List<LR2IRData> entries = DeduplicateIrData(irData);
        if (entries.Count == 0)
        {
            return;
        }
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            foreach (LR2IRData entry in entries)
            {
                songDb.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(e => e.hash) + " = '" + entry.hash + "' AND " + SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(e => e.lr2id) + " = " + entry.lr2id + ";");
                songDb.InsertOrReplace(entry, typeof(LR2SongDBExtended.ir_data));
            }
        });
    }

    public bool TryBulkInsertIrDataForEmptyLr2Id(int lr2Id, IEnumerable<LR2IRData> irData)
    {
        List<LR2IRData> entries = [.. DeduplicateIrData(irData).Where(entry => entry.lr2id == lr2Id)];
        if (entries.Count == 0)
        {
            return true;
        }
        bool inserted = false;
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            string tableName = SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName();
            string lr2IdColumn = SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(row => row.lr2id);
            long existingRows = songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM " + tableName + " WHERE " + lr2IdColumn + " = " + lr2Id + ";");
            if (existingRows != 0)
            {
                return;
            }
            songDb.InsertAll(entries, typeof(LR2SongDBExtended.ir_data));
            inserted = true;
        });
        return inserted;
    }

    private static List<LR2IRData> DeduplicateIrData(IEnumerable<LR2IRData> irData)
    {
        var deduplicated = new Dictionary<string, LR2IRData>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2IRData entry in irData ?? [])
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.hash))
            {
                continue;
            }
            deduplicated[entry.hash + "\u001f" + entry.lr2id] = entry;
        }
        return [.. deduplicated.Values];
    }

    public void ReplaceIrScoreTable(IEnumerable<LR2IRScore> scoreTable)
    {
        List<LR2IRScore> entries = [.. (scoreTable ?? []).Where(score => score != null)];
        ExecuteSongDbTransaction(delegate (LR2SongDBExtended songDb)
        {
            songDb.DropTable<LR2SongDBExtended.ir_score>();
            songDb.CreateTable<LR2SongDBExtended.ir_score>();
            if (entries.Count > 0)
            {
                songDb.InsertAll(entries, typeof(LR2SongDBExtended.ir_score));
            }
            songDb.CreateIndex("ir_score_idx_unsent", SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName(),
            [
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.hash),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.clear),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.combo),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.pg),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.gr),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.gd),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.bd),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.pr),
                SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.minbp)
            ]);
        });
    }

    internal static void EnsureBmsonSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        songDb.CreateTable<LR2SongDBExtended.app_schema_version>();
        RepairableBmsonSchemaIssues issues = AppSchemaPreflightService.AnalyzeRepairableBmsonSchemaIssues(songDb);
        bool rebuildChartDigestMapTable = issues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableMissing)
            || issues.HasFlag(RepairableBmsonSchemaIssues.ChartDigestMapTableInvalid);
        bool rebuildBmsonSongTable = issues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableMissing)
            || issues.HasFlag(RepairableBmsonSchemaIssues.BmsonSongTableInvalid);
        if (rebuildChartDigestMapTable && TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()))
        {
            songDb.DropTable<LR2SongDBExtended.chart_digest_map>();
        }
        if (rebuildBmsonSongTable && TableExists(songDb, SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()))
        {
            songDb.DropTable<LR2SongDBExtended.bmson_song>();
        }

        string chartDigestMapTableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();

        string bmsonSongTableName = SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.bmson_song>();
        EnsureIndex(songDb, "bmson_song_idx_md5", bmsonSongTableName, SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.md5));
        EnsureIndex(songDb, "bmson_song_idx_sha256", bmsonSongTableName, SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.sha256));
        EnsureIndex(songDb, "bmson_song_idx_folder", bmsonSongTableName, SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.folder));
    }

    internal static void EnsureSongLookupIndexes(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string tableName = SQLiteTable<LR2SongDB.song>.GetTableName();
        songDb.CreateTable<LR2SongDB.song>();
        EnsureIndex(songDb, "hashidx", tableName, SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash));
        EnsureIndex(songDb, "parentidx", tableName, SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent));
        EnsureIndex(songDb, "song_idx_folder", tableName, SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder));
        EnsureNocaseIndex(songDb, SongPathNocaseIndexName, tableName, SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path));
    }

    internal static void EnsureFolderLookupIndexes(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string folderTableName = SQLiteTable<LR2SongDB.folder>.GetTableName();
        songDb.CreateTable<LR2SongDB.folder>();
        EnsureNocaseIndex(songDb, FolderPathNocaseIndexName, folderTableName, SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path));
    }

    internal static void EnsureMaintenanceSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string tableName = SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.maintenance>();
        HashSet<string> columns = GetTableColumns(songDb, null, tableName);
        EnsureColumn(songDb, tableName, columns, "lr2_warning_flags", "INTEGER NULL");
        EnsureColumn(songDb, tableName, columns, "lr2_resource_max_relative_cp932_bytes", "INTEGER NULL");
        EnsureColumn(songDb, tableName, columns, "lr2_resource_has_parent_traversal", "INTEGER NULL");
        EnsureNocaseIndex(songDb, MaintenancePathNocaseIndexName, tableName, SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.path));
    }

    internal static void EnsureIrDataSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string tableName = SQLiteTable<LR2SongDBExtended.ir_data>.GetTableName();
        songDb.CreateTable<LR2SongDBExtended.ir_data>();
        songDb.CreateIndex("ir_data_idx", tableName, [SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(e => e.lr2id)]);
        songDb.CreateIndex(
            "ir_data_idx_lr2id_hash",
            tableName,
            [
                SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(e => e.lr2id),
                SQLiteTable<LR2SongDBExtended.ir_data>.GetColumnName(e => e.hash)
            ]);
    }

    internal static void EnsureLr2SongDbSyncStatusSchema(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        songDb.CreateTable<LR2SongDBExtended.lr2_song_db_sync_status>();
    }

    /// <summary>
    /// chart_info テーブルと関連 index を作成または修復します。
    /// </summary>
    /// <param name="songDb">対象 song.db 接続。</param>
    internal static void EnsureChartInfoSchema(LR2SongDBExtended songDb)
    {
        EnsureChartInfoSchemaOnConnection(songDb, stampVersion: true);
    }

    private static void EnsureChartInfoSchemaOnConnection(LR2SongDBExtended songDb, bool stampVersion)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        songDb.CreateTable<LR2SongDBExtended.app_schema_version>();
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        if (TableExists(songDb, tableName) && !IsChartInfoTableCompatible(songDb))
        {
            songDb.DropTable<LR2SongDBExtended.chart_info>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_info>();
        EnsureIndex(songDb, "chart_info_idx_md5", tableName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.md5));
        EnsureIndex(songDb, "chart_info_idx_charthash", tableName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.charthash));
        EnsureIndex(songDb, "chart_info_idx_parser_version", tableName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.parser_version));
        string failureTableName = SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName();
        if (TableExists(songDb, failureTableName) && !IsChartInfoParseFailureTableCompatible(songDb))
        {
            songDb.DropTable<LR2SongDBExtended.chart_info_parse_failure>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_info_parse_failure>();
        EnsureIndex(songDb, "chart_info_parse_failure_idx_sha256", failureTableName, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetColumnName(row => row.sha256));
        EnsureIndex(songDb, "chart_info_parse_failure_idx_parser_version", failureTableName, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetColumnName(row => row.parser_version));
        string importHistoryTableName = SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName();
        if (TableExists(songDb, importHistoryTableName) && !IsChartInfoImportHistoryTableCompatible(songDb))
        {
            songDb.DropTable<LR2SongDBExtended.chart_info_import_history>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_info_import_history>();
        EnsureIndex(songDb, "chart_info_import_history_idx_bundle_sha256", importHistoryTableName, SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetColumnName(row => row.bundle_sha256));
        if (stampVersion)
        {
            SetCurrentAppSchemaVersion(songDb);
        }
    }

    internal static void UpsertChartDigest(LR2SongDBExtended songDb, BMSFile file)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (file == null || string.IsNullOrWhiteSpace(file.hash) || string.IsNullOrWhiteSpace(file.sha256))
        {
            return;
        }
        var row = new LR2SongDBExtended.chart_digest_map
        {
            md5 = file.hash,
            sha256 = file.sha256
        };
        songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_digest_map));
    }

    private static void RepairAppOwnedSchemaOnConnection(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        Dictionary<string, string> reusableDigests = LoadReusableChartDigestMap(songDb);
        EnsureAppOwnedSchema(songDb, stampVersion: false);
        RebuildChartDigestMap(songDb, reusableDigests);
        SetCurrentAppSchemaVersion(songDb);
    }

    /// <summary>
    /// 借用済み song.db connection 上で app-owned schema を準備します。
    /// transaction の開始、commit、rollback は呼び出し側の owner が行います。
    /// </summary>
    internal static void EnsureAppOwnedSchema(LR2SongDBExtended songDb)
    {
        EnsureAppOwnedSchema(songDb, stampVersion: true);
    }

    private static void EnsureAppOwnedSchema(LR2SongDBExtended songDb, bool stampVersion)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        PlaylistPersistenceRepository.EnsureSchemaOnConnection(songDb);
        EnsureBmsonSchema(songDb);
        EnsureMaintenanceSchema(songDb);
        EnsureChartInfoSchemaOnConnection(songDb, stampVersion);
        EnsureIrDataSchema(songDb);
        EnsureSongLookupIndexes(songDb);
        EnsureLr2SongDbSyncStatusSchema(songDb);
        if (stampVersion)
        {
            SetCurrentAppSchemaVersion(songDb);
        }
    }

    internal static void RepairChartDigestMapConsistency(LR2SongDBExtended songDb)
    {
        RepairChartDigestMapConsistency(songDb, null);
    }

    internal static void DeleteChartDigestIfOrphaned(LR2SongDBExtended songDb, string md5, string preservedMd5 = null)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()))
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(preservedMd5) && string.Equals(md5, preservedMd5, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        long count = songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash)
            + " = " + SqlQuote(md5) + ";");
        if (count <= 0)
        {
            songDb.Delete<LR2SongDBExtended.chart_digest_map>(md5);
        }
    }

    internal static void DeleteChartDigestsIfOrphaned(LR2SongDBExtended songDb, IEnumerable<string> md5s, string preservedMd5 = null)
    {
        foreach (string md5 in (md5s ?? []).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            DeleteChartDigestIfOrphaned(songDb, md5, preservedMd5);
        }
    }

    private static bool TableExists(LR2SongDBExtended songDb, string tableName)
    {
        return TableExists(songDb, null, tableName);
    }

    private static bool TableExists(LR2SongDBExtended songDb, string schemaName, string tableName)
    {
        string masterTableName = string.IsNullOrWhiteSpace(schemaName) ? "sqlite_master" : schemaName + ".sqlite_master";
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM " + masterTableName + " WHERE type = 'table' AND name = " + SqlQuote(tableName) + ";") > 0;
    }

    private static bool IndexExists(LR2SongDBExtended songDb, string indexName)
    {
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + SqlQuote(indexName) + ";") > 0;
    }

    private static void EnsureIndex(LR2SongDBExtended songDb, string indexName, string tableName, string columnName)
    {
        if (!IndexExists(songDb, indexName))
        {
            songDb.CreateIndex(indexName, tableName, columnName);
        }
    }

    private static void EnsureNocaseIndex(LR2SongDBExtended songDb, string indexName, string tableName, string columnName)
    {
        if (!IndexExists(songDb, indexName))
        {
            songDb.Execute(
                "CREATE INDEX " + indexName
                + " ON " + tableName + " (" + columnName + " COLLATE NOCASE);");
        }
    }

    private static string GetSongHashByPath(LR2SongDBExtended songDb, string path)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        return songDb.ExecuteScalar<string>(
            "SELECT " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash)
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " = " + SqlQuote(path)
            + " LIMIT 1;");
    }

    private static void BulkUpsertMaintenanceInfos(LR2SongDBExtended songDb, IEnumerable<BMSFileMaintenanceInfo> maintenanceInfos)
    {
        List<BMSFileMaintenanceInfo> rows = [.. (maintenanceInfos ?? [])
            .Where(row => row != null && !string.IsNullOrWhiteSpace(row.path))];
        if (rows.Count == 0)
        {
            return;
        }

        PrepareTempMaintenanceUpsertTable(songDb);
        BulkInsertMaintenanceTempRows(songDb, rows);
        UpsertMaintenanceRowsFromTemp(songDb);
        ClearTempLookupTable(songDb, TempFileScanMaintenanceUpsertTable);
    }

    private static void PrepareTempMaintenanceUpsertTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempFileScanMaintenanceUpsertTable + " ("
            + "hash TEXT, "
            + "path TEXT PRIMARY KEY, "
            + "encoding TEXT, "
            + "is_encoding_fixed INTEGER, "
            + "wav_files_existing INTEGER, "
            + "wav_files_defined INTEGER, "
            + "bga_files_existing INTEGER, "
            + "bga_files_defined INTEGER, "
            + "movie_files_existing INTEGER, "
            + "movie_files_defined INTEGER, "
            + "is_stagefile_existing INTEGER, "
            + "is_stagefile_defined INTEGER, "
            + "is_banner_existing INTEGER, "
            + "is_banner_defined INTEGER, "
            + "is_backbmp_existing INTEGER, "
            + "is_backbmp_defined INTEGER, "
            + "is_files_warning_ignored INTEGER, "
            + "lr2_warning_flags INTEGER, "
            + "lr2_resource_max_relative_cp932_bytes INTEGER, "
            + "lr2_resource_has_parent_traversal INTEGER);");
        ClearTempLookupTable(songDb, TempFileScanMaintenanceUpsertTable);
    }

    private static void BulkInsertMaintenanceTempRows(LR2SongDBExtended songDb, IReadOnlyList<BMSFileMaintenanceInfo> rows)
    {
        string[] columns = GetMaintenanceColumnNames();
        int columnCount = columns.Length;
        const int chunkSize = 30;
        for (int offset = 0; offset < rows.Count; offset += chunkSize)
        {
            List<BMSFileMaintenanceInfo> chunk = rows.Skip(offset).Take(chunkSize).ToList();
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", columnCount)) + ")";
            string placeholders = string.Join(",", chunk.Select(_ => rowPlaceholders));
            var args = new List<object>(chunk.Count * columnCount);
            foreach (BMSFileMaintenanceInfo row in chunk)
            {
                AddMaintenanceInsertArgs(args, row);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempFileScanMaintenanceUpsertTable
                + " (" + string.Join(",", columns) + ") VALUES " + placeholders + ";",
                [.. args]);
        }
    }

    private static void UpsertMaintenanceRowsFromTemp(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName();
        string tempName = "temp." + TempFileScanMaintenanceUpsertTable;
        string[] columns = GetMaintenanceColumnNames();
        string pathColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.path);
        string setClause = string.Join(", ", columns
            .Where(column => !string.Equals(column, pathColumn, StringComparison.Ordinal))
            .Select(column => column + " = (SELECT t." + column + " FROM " + tempName + " t WHERE t.path = " + tableName + "." + pathColumn + ")"));
        songDb.Execute(
            "UPDATE " + tableName
            + " SET " + setClause
            + " WHERE EXISTS (SELECT 1 FROM " + tempName + " t WHERE t.path = "
            + tableName + "." + pathColumn + ");");
        songDb.Execute(
            "INSERT INTO " + tableName
            + " (" + string.Join(",", columns) + ") "
            + "SELECT " + string.Join(",", columns.Select(column => "t." + column)) + " "
            + "FROM " + tempName + " t "
            + "WHERE NOT EXISTS (SELECT 1 FROM " + tableName + " m"
            + " WHERE m." + pathColumn + " = t.path);");
    }

    private static string[] GetMaintenanceColumnNames()
    {
        return
        [
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.hash),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.path),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.encoding),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_encoding_fixed),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.wav_files_existing),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.wav_files_defined),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.bga_files_existing),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.bga_files_defined),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.movie_files_existing),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.movie_files_defined),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_stagefile_existing),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_stagefile_defined),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_banner_existing),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_banner_defined),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_backbmp_existing),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_backbmp_defined),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.is_files_warning_ignored),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.lr2_warning_flags),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.lr2_resource_max_relative_cp932_bytes),
            SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.lr2_resource_has_parent_traversal)
        ];
    }

    private static void AddMaintenanceInsertArgs(List<object> args, BMSFileMaintenanceInfo row)
    {
        args.Add(row.hash);
        args.Add(row.path);
        args.Add(row.encoding);
        args.Add(row.is_encoding_fixed ? 1 : 0);
        args.Add(row.wav_files_existing);
        args.Add(row.wav_files_defined);
        args.Add(row.bga_files_existing);
        args.Add(row.bga_files_defined);
        args.Add(row.movie_files_existing);
        args.Add(row.movie_files_defined);
        args.Add(ToNullableInteger(row.is_stagefile_existing));
        args.Add(ToNullableInteger(row.is_stagefile_defined));
        args.Add(ToNullableInteger(row.is_banner_existing));
        args.Add(ToNullableInteger(row.is_banner_defined));
        args.Add(ToNullableInteger(row.is_backbmp_existing));
        args.Add(ToNullableInteger(row.is_backbmp_defined));
        args.Add(row.is_files_warning_ignored ? 1 : 0);
        args.Add(row.lr2_warning_flags);
        args.Add(row.lr2_resource_max_relative_cp932_bytes);
        args.Add(ToNullableInteger(row.lr2_resource_has_parent_traversal));
    }

    private static int? ToNullableInteger(bool? value)
    {
        return value.HasValue ? (value.Value ? 1 : 0) : null;
    }

    private static void BulkDeleteBmsPaths(LR2SongDBExtended songDb, IEnumerable<string> paths)
    {
        List<string> sourcePaths = BuildExactLookupKeys(paths);
        if (sourcePaths.Count == 0)
        {
            return;
        }
        PrepareTempLookupTable(songDb, TempDeletedBmsPathTable, "path");
        foreach (string path in sourcePaths)
        {
            songDb.Execute("INSERT OR IGNORE INTO temp." + TempDeletedBmsPathTable + " (path) VALUES (?);", path);
        }
        PrepareTempLookupTable(songDb, TempDeletedBmsHashTable, "md5");
        songDb.Execute(
            "INSERT OR IGNORE INTO temp." + TempDeletedBmsHashTable + " (md5) "
            + "SELECT DISTINCT s." + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash)
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName() + " s "
            + "INNER JOIN temp." + TempDeletedBmsPathTable + " d ON d.path = s." + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " WHERE s." + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " IS NOT NULL "
            + "AND TRIM(s." + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + ") <> '';");
        songDb.Execute(
            "DELETE FROM " + SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.path)
            + " IN (SELECT path FROM temp." + TempDeletedBmsPathTable + ");");
        songDb.Execute(
            "DELETE FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " IN (SELECT path FROM temp." + TempDeletedBmsPathTable + ");");
        DeleteChartDigestsIfOrphanedFromTemp(songDb, TempDeletedBmsHashTable);
        ClearTempLookupTable(songDb, TempDeletedBmsHashTable);
        ClearTempLookupTable(songDb, TempDeletedBmsPathTable);
    }

    private static void BulkDeleteBmsonPaths(LR2SongDBExtended songDb, IEnumerable<string> paths)
    {
        List<string> sourcePaths = BuildExactLookupKeys(paths);
        if (sourcePaths.Count == 0)
        {
            return;
        }
        PrepareTempLookupTable(songDb, TempDeletedBmsonPathTable, "path");
        foreach (string path in sourcePaths)
        {
            songDb.Execute("INSERT OR IGNORE INTO temp." + TempDeletedBmsonPathTable + " (path) VALUES (?);", path);
        }
        songDb.Execute(
            "DELETE FROM " + SQLiteTable<LR2SongDBExtended.bmson_song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.bmson_song>.GetColumnName(row => row.path)
            + " IN (SELECT path FROM temp." + TempDeletedBmsonPathTable + ");");
        songDb.Execute(
            "DELETE FROM " + SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(row => row.path)
            + " IN (SELECT path FROM temp." + TempDeletedBmsonPathTable + ");");
        ClearTempLookupTable(songDb, TempDeletedBmsonPathTable);
    }

    private static List<string> BuildExactLookupKeys(IEnumerable<string> source)
    {
        return [.. (source ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)];
    }

    private static void PrepareTempLookupTable(LR2SongDBExtended songDb, string tableName, string columnName)
    {
        songDb.Execute("CREATE TEMP TABLE IF NOT EXISTS " + tableName + " (" + columnName + " TEXT PRIMARY KEY);");
        ClearTempLookupTable(songDb, tableName);
    }

    private static void ClearTempLookupTable(LR2SongDBExtended songDb, string tableName)
    {
        songDb.Execute("DELETE FROM temp." + tableName + ";");
    }

    private static void DeleteChartDigestsIfOrphanedFromTemp(LR2SongDBExtended songDb, string tempHashTableName)
    {
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()))
        {
            return;
        }
        songDb.Execute(
            "DELETE FROM " + SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetColumnName(row => row.md5)
            + " IN ("
            + "SELECT h.md5 FROM temp." + tempHashTableName + " h "
            + "WHERE h.md5 IS NOT NULL AND TRIM(h.md5) <> '' "
            + "AND NOT EXISTS ("
            + "SELECT 1 FROM " + SQLiteTable<LR2SongDB.song>.GetTableName() + " s "
            + "WHERE s." + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " = h.md5"
            + ")"
            + ");");
    }

    private static bool IsChartInfoSchemaCurrent(LR2SongDBExtended songDb)
    {
        if (songDb == null)
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()))
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName()) || !IsChartInfoTableCompatible(songDb))
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName()) || !IsChartInfoParseFailureTableCompatible(songDb))
        {
            return false;
        }
        if (!TableExists(songDb, SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName()) || !IsChartInfoImportHistoryTableCompatible(songDb))
        {
            return false;
        }
        long count = songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName(row => row.name)
            + " = " + SqlQuote(AppSchemaVersionName)
            + " AND " + SQLiteTable<LR2SongDBExtended.app_schema_version>.GetColumnName(row => row.version)
            + " >= " + CurrentAppSchemaVersion + ";");
        return count > 0;
    }

    private static void SetCurrentAppSchemaVersion(LR2SongDBExtended songDb)
    {
        songDb.CreateTable<LR2SongDBExtended.app_schema_version>();
        songDb.InsertOrReplace(new LR2SongDBExtended.app_schema_version
        {
            name = AppSchemaVersionName,
            version = CurrentAppSchemaVersion
        }, typeof(LR2SongDBExtended.app_schema_version));
    }

    private static string BuildChartInfoMetadataBundleImportKey(string bundleSha256)
    {
        return (bundleSha256 ?? string.Empty).Trim().ToLowerInvariant() + ":" + CurrentChartInfoParserVersion;
    }

    private static bool IsChartInfoMetadataBundleImported(LR2SongDBExtended songDb, string importKey)
    {
        return songDb.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM " + SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetColumnName(row => row.import_key)
            + " = ?;",
            importKey) > 0L;
    }

    private static ChartInfoMetadataBundleManifest LoadChartInfoMetadataBundleManifest(LR2SongDBExtended songDb)
    {
        RequireAttachedTableColumns(
            songDb,
            "bundle",
            "chart_info_metadata_bundle",
            [
                "bundle_id",
                "format_version",
                "generated_at",
                "chart_info_schema_version",
                "chart_info_parser_version",
                "chart_info_count",
                "chart_digest_count"
            ]);
        RequireAttachedTableColumns(songDb, "bundle", "chart_info", ChartInfoColumnList.Split([", "], StringSplitOptions.None));
        RequireAttachedTableColumns(songDb, "bundle", "chart_digest_map", ["md5", "sha256"]);
        ChartInfoMetadataBundleManifest manifest = songDb.Query<ChartInfoMetadataBundleManifest>(
            "SELECT bundle_id, format_version, generated_at, chart_info_schema_version, chart_info_parser_version, chart_info_count, chart_digest_count "
            + "FROM bundle.chart_info_metadata_bundle ORDER BY bundle_id COLLATE NOCASE ASC LIMIT 1;").FirstOrDefault();
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.bundle_id))
        {
            throw new InvalidDataException("chart_info metadata bundle manifest is missing.");
        }
        if (manifest.format_version != ChartInfoMetadataBundleFormatVersion)
        {
            throw new InvalidDataException("Unsupported chart_info metadata bundle format version: " + manifest.format_version);
        }
        if (manifest.chart_info_schema_version < CurrentChartInfoSchemaVersion)
        {
            throw new InvalidDataException("Stale chart_info metadata bundle schema version: " + manifest.chart_info_schema_version);
        }
        if (manifest.chart_info_parser_version < CurrentChartInfoParserVersion)
        {
            throw new InvalidDataException("Stale chart_info metadata bundle parser version: " + manifest.chart_info_parser_version);
        }
        return manifest;
    }

    private static void PrepareChartInfoMetadataImportTempTables(LR2SongDBExtended songDb)
    {
        DropChartInfoMetadataImportTempTables(songDb);
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_info AS SELECT "
            + "lower(trim(b.sha256)) AS sha256, "
            + "lower(trim(b.md5)) AS md5, "
            + NormalizedOptionalSha256Expression("b.charthash") + " AS charthash, "
            + "b.level AS level, "
            + "b.difficulty AS difficulty, "
            + "b.difficulty_defined AS difficulty_defined, "
            + "b.mainbpm AS mainbpm, "
            + "b.maxbpm AS maxbpm, "
            + "b.minbpm AS minbpm, "
            + "b.length AS length, "
            + "b.mode AS mode, "
            + "b.judge AS judge, "
            + "b.bga AS bga, "
            + "b.exlevel AS exlevel, "
            + "b.feature AS feature, "
            + "b.notes AS notes, "
            + "b.n AS n, "
            + "b.ln AS ln, "
            + "b.s AS s, "
            + "b.ls AS ls, "
            + "b.total AS total, "
            + "b.total_defined AS total_defined, "
            + "b.density AS density, "
            + "b.peakdensity AS peakdensity, "
            + "b.enddensity AS enddensity, "
            + "b.distribution AS distribution, "
            + "b.speedchange AS speedchange, "
            + "b.speedchange_count AS speedchange_count, "
            + "b.lanenotes AS lanenotes, "
            + "b.parser_version AS parser_version, "
            + "b.updated_at AS updated_at "
            + "FROM bundle.chart_info b WHERE "
            + ValidSha256Condition("b.sha256")
            + " AND " + ValidMd5Condition("b.md5")
            + " AND b.parser_version >= " + CurrentChartInfoParserVersion + ";");
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_info_to_import AS "
            + "SELECT s.* FROM temp.chart_info_metadata_import_info s "
            + "LEFT JOIN main.chart_info d ON d.sha256 = s.sha256 "
            + "WHERE d.sha256 IS NULL OR IFNULL(d.parser_version, 0) < " + CurrentChartInfoParserVersion + ";");
        songDb.Execute("CREATE TEMP TABLE chart_info_metadata_import_digest_source (md5 TEXT PRIMARY KEY, sha256 TEXT);");
        songDb.Execute(
            "INSERT OR IGNORE INTO temp.chart_info_metadata_import_digest_source (md5, sha256) "
            + "SELECT lower(trim(d.md5)), lower(trim(d.sha256)) FROM bundle.chart_digest_map d WHERE "
            + ValidMd5Condition("d.md5")
            + " AND " + ValidSha256Condition("d.sha256")
            + " ORDER BY lower(trim(d.md5)) COLLATE NOCASE ASC, lower(trim(d.sha256)) COLLATE NOCASE ASC;");
        songDb.Execute(
            "INSERT OR IGNORE INTO temp.chart_info_metadata_import_digest_source (md5, sha256) "
            + "SELECT md5, sha256 FROM temp.chart_info_metadata_import_info WHERE "
            + ValidMd5Condition("md5")
            + " AND " + ValidSha256Condition("sha256")
            + " ORDER BY md5 COLLATE NOCASE ASC, sha256 COLLATE NOCASE ASC;");
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_digest_to_import AS "
            + "SELECT s.* FROM temp.chart_info_metadata_import_digest_source s "
            + "LEFT JOIN main.chart_digest_map d ON d.md5 = s.md5 "
            + "WHERE d.md5 IS NULL;");
        songDb.Execute(
            "CREATE TEMP TABLE chart_info_metadata_import_failure_to_clear AS "
            + "SELECT DISTINCT f.md5 FROM main.chart_info_parse_failure f "
            + "JOIN temp.chart_info_metadata_import_info s ON s.md5 = f.md5;");
    }

    private static void DropChartInfoMetadataImportTempTables(LR2SongDBExtended songDb)
    {
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_failure_to_clear;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_digest_to_import;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_digest_source;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_info_to_import;");
        songDb.Execute("DROP TABLE IF EXISTS temp.chart_info_metadata_import_info;");
    }

    private static string NormalizedOptionalSha256Expression(string column)
    {
        return "CASE WHEN " + ValidSha256Condition(column) + " THEN lower(trim(" + column + ")) ELSE NULL END";
    }

    private static string ValidSha256Condition(string column)
    {
        return column + " IS NOT NULL AND length(trim(" + column + ")) = 64 AND lower(trim(" + column + ")) NOT GLOB '*[^0-9a-f]*'";
    }

    private static string ValidMd5Condition(string column)
    {
        return column + " IS NOT NULL AND length(trim(" + column + ")) = 32 AND lower(trim(" + column + ")) NOT GLOB '*[^0-9a-f]*'";
    }

    private static void RequireAttachedTableColumns(LR2SongDBExtended songDb, string schemaName, string tableName, IEnumerable<string> requiredColumns)
    {
        if (!TableExists(songDb, schemaName, tableName))
        {
            throw new InvalidDataException("Attached chart_info metadata bundle does not contain required table: " + tableName);
        }
        HashSet<string> columns = GetTableColumns(songDb, schemaName, tableName);
        foreach (string column in requiredColumns ?? [])
        {
            if (!columns.Contains(column))
            {
                throw new InvalidDataException("Attached chart_info metadata bundle table " + tableName + " does not contain required column: " + column);
            }
        }
    }

    private static HashSet<string> GetTableColumns(LR2SongDBExtended songDb, string schemaName, string tableName)
    {
        string pragmaPrefix = string.IsNullOrWhiteSpace(schemaName) ? string.Empty : schemaName + ".";
        return new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA " + pragmaPrefix + "table_info('" + tableName.Replace("'", "''") + "');")
                .Select(row => row.name),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void EnsureColumn(
        LR2SongDBExtended songDb,
        string tableName,
        ISet<string> columns,
        string columnName,
        string columnType)
    {
        if (columns.Contains(columnName))
        {
            return;
        }
        songDb.Execute("ALTER TABLE \"" + tableName.Replace("\"", "\"\"") + "\" ADD COLUMN \"" + columnName.Replace("\"", "\"\"") + "\" " + columnType + ";");
        columns.Add(columnName);
    }

    private static Dictionary<string, string> LoadReusableChartDigestMap(LR2SongDBExtended songDb)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return result;
        }
        string tableSql = songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + SqlQuote(tableName) + ";");
        if (string.IsNullOrWhiteSpace(tableSql)
            || tableSql.IndexOf("md5", StringComparison.OrdinalIgnoreCase) < 0
            || tableSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return result;
        }
        foreach (ChartDigestQueryRow row in songDb.Query<ChartDigestQueryRow>("SELECT md5, sha256 FROM " + tableName + " WHERE md5 IS NOT NULL AND TRIM(md5) <> '' AND sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            string md5 = row.md5;
            string sha256 = row.sha256;
            if (!string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
            {
                result[md5] = sha256;
            }
        }
        return result;
    }

    private static List<string> NormalizeChartInfoLookupKeys(IEnumerable<string> values)
    {
        return [.. (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string NormalizeLookupKey(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> LoadChartDigestMapForCandidateSummary(LR2SongDBExtended songDb)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return result;
        }
        foreach (ChartDigestQueryRow row in songDb.Query<ChartDigestQueryRow>("SELECT md5, sha256 FROM " + tableName + " WHERE md5 IS NOT NULL AND TRIM(md5) <> '' AND sha256 IS NOT NULL AND TRIM(sha256) <> '';"))
        {
            string md5 = NormalizeLookupKey(row.md5);
            string sha256 = NormalizeLookupKey(row.sha256);
            if (!string.IsNullOrWhiteSpace(md5) && !string.IsNullOrWhiteSpace(sha256))
            {
                result[md5] = sha256;
            }
        }
        return result;
    }

    private static void ClassifyChartInfoBackfillCandidate(
        ChartInfoBackfillCandidateSummary summary,
        string md5,
        string sha256,
        HashSet<string> currentChartInfoSha256,
        HashSet<string> anyChartInfoSha256,
        HashSet<string> currentParseFailureMd5,
        bool missingDigestWhenShaMissing)
    {
        if (!string.IsNullOrWhiteSpace(sha256) && currentChartInfoSha256.Contains(sha256))
        {
            summary.CurrentChartInfoOwnerCount++;
            return;
        }
        if (!string.IsNullOrWhiteSpace(md5) && currentParseFailureMd5.Contains(md5))
        {
            summary.CurrentParseFailureOwnerCount++;
            return;
        }
        summary.CandidateOwnerCount++;
        if (string.IsNullOrWhiteSpace(sha256))
        {
            if (missingDigestWhenShaMissing)
            {
                summary.MissingDigestOwnerCount++;
            }
            else
            {
                summary.MissingChartInfoOwnerCount++;
            }
            return;
        }
        if (anyChartInfoSha256.Contains(sha256))
        {
            summary.StaleChartInfoOwnerCount++;
            return;
        }
        summary.MissingChartInfoOwnerCount++;
    }

    private static IEnumerable<LR2SongDBExtended.chart_info> QueryChartInfosByColumn(LR2SongDBExtended songDb, string columnName, IReadOnlyList<string> keys, bool orderBySha256)
    {
        if (songDb == null || keys == null || keys.Count == 0)
        {
            yield break;
        }
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        for (int offset = 0; offset < keys.Count; offset += ChartInfoLookupChunkSize)
        {
            List<string> chunk = [.. keys.Skip(offset).Take(ChartInfoLookupChunkSize)];
            if (chunk.Count == 0)
            {
                continue;
            }
            string placeholders = string.Join(", ", chunk.Select(_ => "?"));
            string sql = "SELECT * FROM " + tableName + " WHERE " + columnName + " IN (" + placeholders + ")";
            if (orderBySha256)
            {
                sql += " ORDER BY sha256 COLLATE NOCASE ASC";
            }
            foreach (LR2SongDBExtended.chart_info row in songDb.Query<LR2SongDBExtended.chart_info>(sql, [.. chunk.Cast<object>()]))
            {
                yield return row;
            }
        }
    }

    private List<LR2SongDBExtended.chart_info> LoadChartInfoRowsByColumn(string columnName, IReadOnlyList<string> keys, bool orderBySha256)
    {
        if (TryLoadChartInfoRowsByColumnReadOnly(columnName, keys, orderBySha256, out List<LR2SongDBExtended.chart_info> rows))
        {
            return rows;
        }

        using LR2SongDBExtended songDb = OpenSongDb();
        EnsureChartInfoSchema(songDb);
        return [.. QueryChartInfosByColumn(songDb, columnName, keys, orderBySha256)];
    }

    private bool TryLoadChartInfoRowsByColumnReadOnly(string columnName, IReadOnlyList<string> keys, bool orderBySha256, out List<LR2SongDBExtended.chart_info> rows)
    {
        rows = [];
        try
        {
            using LR2SongDBExtended songDb = OpenSongDbReadOnly();
            if (!IsChartInfoSchemaCurrent(songDb))
            {
                return false;
            }
            rows = [.. QueryChartInfosByColumn(songDb, columnName, keys, orderBySha256)];
            return true;
        }
        catch (SQLiteException)
        {
            rows = [];
            return false;
        }
    }

    private static List<LR2SongDBExtended.chart_info_parse_failure> NormalizeChartInfoParseFailureRows(IEnumerable<LR2SongDBExtended.chart_info_parse_failure> rows)
    {
        return [.. (rows ?? []).Where(row => row != null && !string.IsNullOrWhiteSpace(row.md5))];
    }

    private static int SafeExecuteScalarInt(LR2SongDBExtended songDb, string sql)
    {
        if (songDb == null || string.IsNullOrWhiteSpace(sql))
        {
            return 0;
        }
        try
        {
            long value = songDb.ExecuteScalar<long>(sql);
            if (value <= 0L)
            {
                return 0;
            }
            return value >= int.MaxValue ? int.MaxValue : (int)value;
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildCurrentParseFailureJoinCondition(string alias, TimeSpan parseTimeout)
    {
        string effectiveAlias = string.IsNullOrWhiteSpace(alias) ? string.Empty : alias.Trim() + ".";
        long timeoutMs = Math.Max(0L, (long)Math.Ceiling(parseTimeout.TotalMilliseconds));
        return effectiveAlias + "parser_version = " + CurrentChartInfoParserVersion
            + " AND ("
            + effectiveAlias + "failure_kind IS NULL"
            + " OR lower(" + effectiveAlias + "failure_kind) <> 'timeout'"
            + " OR COALESCE(" + effectiveAlias + "parse_timeout_ms, -1) >= " + timeoutMs
            + ")";
    }

    private static bool IsCurrentChartInfoParseFailure(LR2SongDBExtended.chart_info_parse_failure row, TimeSpan parseTimeout)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.md5))
        {
            return false;
        }
        return IsCurrentChartInfoParseFailure(row.parser_version, row.failure_kind, row.parse_timeout_ms, parseTimeout);
    }

    private static bool IsCurrentChartInfoParseFailure(int parserVersion, string failureKind, int? parseTimeoutMs, TimeSpan parseTimeout)
    {
        if (parserVersion != CurrentChartInfoParserVersion)
        {
            return false;
        }
        if (!string.Equals(failureKind, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!parseTimeoutMs.HasValue)
        {
            return false;
        }
        long timeoutMs = Math.Max(0L, (long)Math.Ceiling(parseTimeout.TotalMilliseconds));
        return parseTimeoutMs.Value >= timeoutMs;
    }

    private static void RepairChartDigestMapConsistency(LR2SongDBExtended songDb, Dictionary<string, string> reusableDigests)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        string songTableName = SQLiteTable<LR2SongDB.song>.GetTableName();
        if (!TableExists(songDb, songTableName))
        {
            RebuildChartDigestMap(songDb, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            return;
        }
        Dictionary<string, string> digests = reusableDigests != null
            ? new Dictionary<string, string>(reusableDigests, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SongDigestSourceRow row in songDb.Query<SongDigestSourceRow>(
                     "SELECT DISTINCT "
                     + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.hash) + " AS md5, "
                     + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.path) + " AS path "
                     + "FROM " + songTableName + " WHERE "
                     + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.hash) + " IS NOT NULL AND TRIM("
                     + SQLiteTable<LR2SongDB.song>.GetColumnName(song => song.hash) + ") <> '';"))
        {
            string md5 = row.md5;
            string path = row.path;
            if (string.IsNullOrWhiteSpace(md5) || digests.ContainsKey(md5))
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(path) && LongPathFileSystem.FileExists(path))
            {
                digests[md5] = BMSFile.GetSHA256Hash(path);
            }
        }
        RebuildChartDigestMap(songDb, digests);
    }

    private static bool IsChartInfoTableCompatible(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return false;
        }
        var columns = new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');")
                .Select(row => row.name),
            StringComparer.OrdinalIgnoreCase);
        string[] requiredColumns =
        [
            "sha256",
            "md5",
            "charthash",
            "level",
            "difficulty",
            "difficulty_defined",
            "mainbpm",
            "maxbpm",
            "minbpm",
            "length",
            "mode",
            "judge",
            "bga",
            "exlevel",
            "feature",
            "notes",
            "n",
            "ln",
            "s",
            "ls",
            "total",
            "total_defined",
            "density",
            "peakdensity",
            "enddensity",
            "distribution",
            "speedchange",
            "speedchange_count",
            "lanenotes",
            "parser_version",
            "updated_at"
        ];
        return requiredColumns.All(columnName => columns.Contains(columnName));
    }

    private static bool IsChartInfoParseFailureTableCompatible(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info_parse_failure>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return false;
        }
        var columns = new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');")
                .Select(row => row.name),
            StringComparer.OrdinalIgnoreCase);
        string[] requiredColumns =
        [
            "md5",
            "sha256",
            "path",
            "parser_version",
            "failure_kind",
            "exception_type",
            "message",
            "parse_timeout_ms",
            "updated_at"
        ];
        return requiredColumns.All(columnName => columns.Contains(columnName));
    }

    private static bool IsChartInfoImportHistoryTableCompatible(LR2SongDBExtended songDb)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info_import_history>.GetTableName();
        if (!TableExists(songDb, tableName))
        {
            return false;
        }
        var columns = new HashSet<string>(
            songDb.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');")
                .Select(row => row.name),
            StringComparer.OrdinalIgnoreCase);
        string[] requiredColumns =
        [
            "import_key",
            "bundle_id",
            "bundle_sha256",
            "parser_version",
            "chart_info_imported_count",
            "chart_digest_imported_count",
            "failure_cleared_count",
            "imported_at"
        ];
        return requiredColumns.All(columnName => columns.Contains(columnName));
    }

    private static void RebuildChartDigestMap(LR2SongDBExtended songDb, IDictionary<string, string> digests)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_digest_map>.GetTableName();
        if (TableExists(songDb, tableName))
        {
            songDb.DropTable<LR2SongDBExtended.chart_digest_map>();
        }
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        foreach (KeyValuePair<string, string> item in (digests ?? new Dictionary<string, string>()).Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value)))
        {
            songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
            {
                md5 = item.Key,
                sha256 = item.Value
            }, typeof(LR2SongDBExtended.chart_digest_map));
        }
    }

    private sealed class ChartDigestQueryRow
    {
        public string md5 { get; set; }

        public string sha256 { get; set; }
    }

    private sealed class ChartInfoSummaryRow
    {
        public string sha256 { get; set; }

        public int parser_version { get; set; }
    }

    private sealed class ChartInfoParseFailureSummaryRow
    {
        public string md5 { get; set; }

        public int parser_version { get; set; }

        public string failure_kind { get; set; }

        public int? parse_timeout_ms { get; set; }
    }

    private sealed class ChartInfoOwnerMd5Row
    {
        public string md5 { get; set; }
    }

    private sealed class BmsonChartInfoOwnerRow
    {
        public string md5 { get; set; }

        public string sha256 { get; set; }
    }

    private sealed class ChartInfoMetadataBundleManifest
    {
        public string bundle_id { get; set; }

        public int format_version { get; set; }

        public string generated_at { get; set; }

        public int chart_info_schema_version { get; set; }

        public int chart_info_parser_version { get; set; }

        public int chart_info_count { get; set; }

        public int chart_digest_count { get; set; }
    }

    private sealed class TableInfoRow
    {
        public string name { get; set; }
    }

    private sealed class SongDigestSourceRow
    {
        public string md5 { get; set; }

        public string path { get; set; }
    }
}
