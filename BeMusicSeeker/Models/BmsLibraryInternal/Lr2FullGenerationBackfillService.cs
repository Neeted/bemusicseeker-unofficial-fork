using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FullGenerationBackfillRequest
{
    public string Signature { get; set; }

    public string RunId { get; set; }

    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderFilePaths { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderDiscoveryDirectories { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderPruneDirectories { get; set; } = [];

    public bool Lr2FolderFileDiscoveryComplete { get; set; }

    public IReadOnlyCollection<BMSFile> SongRows { get; set; } = [];

    public IReadOnlyCollection<string> TextFileDirectories { get; set; } = [];

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class Lr2FullGenerationBackfillResult
{
    public int TotalCount { get; set; }

    public int ProcessedCount { get; set; }

    public string FinalStage { get; set; }

    public string IncompleteReason { get; set; }

    public Lr2NormalFolderDbSyncResult NormalFolderSyncResult { get; set; }

    public Lr2FolderFileDbSyncResult Lr2FolderFileSyncResult { get; set; }

    public int Lr2FolderFileProcessedCount { get; set; }

    public int SongRowProcessedCount { get; set; }

    public int SongRowParseFailureCount { get; set; }

    public int SongRowChartInfoAppliedCount { get; set; }

    public int SongRowLr2CompatibilityAppliedCount { get; set; }

    public IReadOnlyList<BMSFileMaintenanceInfo> Lr2CompatibilityMaintenanceInfos { get; set; } = [];

    public Lr2StartupScanDiagnosticResult StartupScanDiagnosticResult { get; set; }

    public long ElapsedMs { get; set; }
}

internal sealed class Lr2StartupScanDiagnosticResult(
    int noRootSetBlockerCount,
    int missingCurrentSongRowCount,
    int dateMissingSongRowCount,
    int unknownRootSongRowCount,
    int dateMissingFolderRowCount,
    int unknownRootFolderRowCount)
{
    public int NoRootSetBlockerCount { get; } = noRootSetBlockerCount;

    public int MissingCurrentSongRowCount { get; } = missingCurrentSongRowCount;

    public int DateMissingSongRowCount { get; } = dateMissingSongRowCount;

    public int UnknownRootSongRowCount { get; } = unknownRootSongRowCount;

    public int DateMissingFolderRowCount { get; } = dateMissingFolderRowCount;

    public int UnknownRootFolderRowCount { get; } = unknownRootFolderRowCount;

    public int TotalBlockerCount => NoRootSetBlockerCount
        + MissingCurrentSongRowCount
        + DateMissingSongRowCount
        + UnknownRootSongRowCount
        + DateMissingFolderRowCount
        + UnknownRootFolderRowCount;

    public bool IsClean => TotalBlockerCount == 0;

    public string ToLogDetail()
    {
        return "startup_scan_blockers"
            + " noRootSet=" + NoRootSetBlockerCount
            + " missingSongRows=" + MissingCurrentSongRowCount
            + " dateMissingSongRows=" + DateMissingSongRowCount
            + " unknownRootSongRows=" + UnknownRootSongRowCount
            + " dateMissingFolderRows=" + DateMissingFolderRowCount
            + " unknownRootFolderRows=" + UnknownRootFolderRowCount;
    }
}

internal static class Lr2FullGenerationBackfillService
{
    internal const string CompletedStage = "completed";

    internal const string StartupScanBlockersStage = "startup_scan_blockers";

    internal const string StartupScanBlockersReason = "startup_scan_blockers_detected";

    internal static Lr2FullGenerationBackfillResult Run(
        LR2SongDBExtended songDb,
        Lr2FullGenerationBackfillRequest request)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2FullGenerationBackfillRequest();
        var stopwatch = Stopwatch.StartNew();
        List<string> roots = [.. (request.RootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> chartPaths = [.. (request.ChartPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> folderInfoFilePaths = [.. (request.FolderInfoFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> lr2FolderFilePaths = [.. (request.Lr2FolderFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> lr2FolderDiscoveryDirectories = [.. (request.Lr2FolderDiscoveryDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> lr2FolderPruneDirectories = [.. (request.Lr2FolderPruneDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<BMSFile> songRows = [.. (request.SongRows ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.path))];
        HashSet<string> textFileDirectories = [.. (request.TextFileDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        int normalFolderTargetCount = roots.Count > 0
            ? roots.Count + chartPaths.Count + folderInfoFilePaths.Count
            : 0;
        int totalCount = normalFolderTargetCount + lr2FolderFilePaths.Count + songRows.Count;

        Lr2FullGenerationStatusService.MarkRunning(
            songDb,
            request.Signature,
            request.RunId,
            totalCount,
            stage: "normal_folders",
            nowUtc: request.StartedAtUtc);

        Lr2NormalFolderDbSyncResult normalFolderResult = null;
        int normalFolderProcessedCount = 0;
        if (roots.Count > 0)
        {
            normalFolderResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = roots,
                ChartPaths = chartPaths,
                FolderInfoFilePaths = folderInfoFilePaths,
                AllowPrune = true,
                GeneratedAtUtc = request.StartedAtUtc
            });
            normalFolderProcessedCount = normalFolderTargetCount;
        }

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: normalFolderProcessedCount,
            totalCount: totalCount,
            stage: "normal_folders_completed",
            nowUtc: DateTime.UtcNow);

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: normalFolderProcessedCount,
            totalCount: totalCount,
            stage: "lr2folder_files",
            nowUtc: DateTime.UtcNow);

        Lr2FolderFileDbSyncResult lr2FolderFileResult = null;
        int lr2FolderFileProcessedCount = 0;
        if (lr2FolderDiscoveryDirectories.Count > 0)
        {
            Lr2FolderFileSyncItemsResult syncItems = CreateLr2FolderFileSyncItems(lr2FolderFilePaths);
            lr2FolderFileResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items = syncItems.Items,
                ScopeDirectories = lr2FolderPruneDirectories,
                AllowPrune = lr2FolderPruneDirectories.Count > 0
                    && request.Lr2FolderFileDiscoveryComplete
                    && !syncItems.HasReadFailures,
                GeneratedAtUtc = request.StartedAtUtc
            });
            lr2FolderFileProcessedCount = lr2FolderFileResult.ItemCount;
        }
        int folderProcessedCount = normalFolderProcessedCount + lr2FolderFileProcessedCount;

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: folderProcessedCount,
            totalCount: totalCount,
            stage: "lr2folder_files_completed",
            nowUtc: DateTime.UtcNow);

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: folderProcessedCount,
            totalCount: totalCount,
            stage: "song_rows",
            nowUtc: DateTime.UtcNow);

        SongRowBackfillResult songRowResult = UpsertSongRows(songDb, songRows, textFileDirectories);
        int processedCount = folderProcessedCount + songRowResult.ProcessedCount;

        Lr2FullGenerationStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: processedCount,
            totalCount: totalCount,
            stage: "song_rows_completed",
            nowUtc: DateTime.UtcNow);

        Lr2StartupScanDiagnosticResult diagnosticResult = DiagnoseStartupScanBlockers(
            songDb,
            roots,
            lr2FolderDiscoveryDirectories,
            songRows);
        string finalStage;
        string incompleteReason;
        if (diagnosticResult.IsClean)
        {
            Lr2FullGenerationStatusService.MarkCompleted(
                songDb,
                request.Signature,
                request.RunId,
                totalCount,
                nowUtc: DateTime.UtcNow);
            finalStage = CompletedStage;
            incompleteReason = null;
        }
        else
        {
            Lr2FullGenerationStatusService.MarkIncomplete(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: processedCount,
                totalCount,
                stage: StartupScanBlockersStage,
                detail: StartupScanBlockersReason + " " + diagnosticResult.ToLogDetail(),
                nowUtc: DateTime.UtcNow);
            finalStage = StartupScanBlockersStage;
            incompleteReason = StartupScanBlockersReason;
        }

        stopwatch.Stop();
        return new Lr2FullGenerationBackfillResult
        {
            TotalCount = totalCount,
            ProcessedCount = processedCount,
            FinalStage = finalStage,
            IncompleteReason = incompleteReason,
            NormalFolderSyncResult = normalFolderResult,
            Lr2FolderFileSyncResult = lr2FolderFileResult,
            Lr2FolderFileProcessedCount = lr2FolderFileProcessedCount,
            SongRowProcessedCount = songRowResult.ProcessedCount,
            SongRowParseFailureCount = songRowResult.ParseFailureCount,
            SongRowChartInfoAppliedCount = songRowResult.ChartInfoAppliedCount,
            SongRowLr2CompatibilityAppliedCount = songRowResult.Lr2CompatibilityAppliedCount,
            Lr2CompatibilityMaintenanceInfos = songRowResult.Lr2CompatibilityMaintenanceInfos,
            StartupScanDiagnosticResult = diagnosticResult,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static Lr2StartupScanDiagnosticResult DiagnoseStartupScanBlockers(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> rootDirectories,
        IReadOnlyCollection<string> lr2FolderDiscoveryDirectories,
        IReadOnlyCollection<BMSFile> currentSongRows)
    {
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        List<string> roots = [.. (rootDirectories ?? [])
            .Select(NormalizeDirectoryPathOrNull)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> lr2FolderRoots = [.. (lr2FolderDiscoveryDirectories ?? [])
            .Select(NormalizeDirectoryPathOrNull)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> allFolderRoots = [.. roots
            .Concat(lr2FolderRoots)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        var currentPaths = new HashSet<string>(
            (currentSongRows ?? []).Select(row => NormalizeFilePathOrNull(row?.path)).Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, StartupDiagnosticSongRow> rowsByPath = songDb.Query<StartupDiagnosticSongRow>(
                "SELECT "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " AS Path, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " AS Date"
                + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName() + ";")
            .Where(row => !string.IsNullOrWhiteSpace(row?.Path))
            .GroupBy(row => NormalizeFilePathOrNull(row.Path) ?? row.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        int noRootSetBlockerCount = roots.Count == 0 ? 1 : 0;
        int missingCurrentSongRowCount = 0;
        int dateMissingSongRowCount = 0;
        foreach (string currentPath in currentPaths)
        {
            if (!rowsByPath.TryGetValue(currentPath, out StartupDiagnosticSongRow row))
            {
                missingCurrentSongRowCount++;
                continue;
            }
            if (!row.Date.HasValue || row.Date.GetValueOrDefault() <= 0)
            {
                dateMissingSongRowCount++;
            }
        }

        int unknownRootSongRowCount = 0;
        if (roots.Count > 0)
        {
            foreach (string path in rowsByPath.Keys)
            {
                if (!IsUnderAnyRoot(path, roots))
                {
                    unknownRootSongRowCount++;
                }
            }
        }

        int dateMissingFolderRowCount = 0;
        int unknownRootFolderRowCount = 0;
        foreach (StartupDiagnosticFolderRow row in songDb.Query<StartupDiagnosticFolderRow>(
            "SELECT "
            + SQLiteTable<LR2SongDB.folder>.GetColumnName(folder => folder.path) + " AS Path, "
            + SQLiteTable<LR2SongDB.folder>.GetColumnName(folder => folder.type) + " AS Type, "
            + SQLiteTable<LR2SongDB.folder>.GetColumnName(folder => folder.date) + " AS Date"
            + " FROM " + SQLiteTable<LR2SongDB.folder>.GetTableName() + ";"))
        {
            if (string.IsNullOrWhiteSpace(row?.Path))
            {
                continue;
            }

            if (!row.Date.HasValue || row.Date.GetValueOrDefault() <= 0)
            {
                dateMissingFolderRowCount++;
            }

            string diagnosticPath = NormalizeFolderDiagnosticPath(row.Path);
            if (string.IsNullOrWhiteSpace(diagnosticPath))
            {
                continue;
            }

            IReadOnlyList<string> scopeRoots = row.Type.GetValueOrDefault() == 1
                ? roots
                : allFolderRoots;
            if (scopeRoots.Count > 0 && !IsUnderAnyRoot(diagnosticPath, scopeRoots))
            {
                unknownRootFolderRowCount++;
            }
        }

        return new Lr2StartupScanDiagnosticResult(
            noRootSetBlockerCount,
            missingCurrentSongRowCount,
            dateMissingSongRowCount,
            unknownRootSongRowCount,
            dateMissingFolderRowCount,
            unknownRootFolderRowCount);
    }

    private static bool IsUnderAnyRoot(string filePath, IEnumerable<string> roots)
    {
        string normalizedFilePath = NormalizeFilePathOrNull(filePath);
        if (string.IsNullOrWhiteSpace(normalizedFilePath))
        {
            return false;
        }
        foreach (string root in roots ?? [])
        {
            if (Lr2FolderPath.IsSameOrDescendant(normalizedFilePath, root))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeFilePathOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeDirectoryPathOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Lr2FolderPath.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeFolderDiagnosticPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return string.Equals(Path.GetExtension(path), ".lr2folder", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path)
                : Lr2FolderPath.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static Lr2FolderFileSyncItemsResult CreateLr2FolderFileSyncItems(IEnumerable<string> filePaths)
    {
        var items = new List<Lr2FolderFileSyncItem>();
        bool hasReadFailures = false;
        foreach (string filePath in filePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            Lr2FolderFileSyncItem item = CreateLr2FolderFileSyncItem(filePath);
            if (item.LastWriteTimeUtc == null)
            {
                hasReadFailures = true;
            }
            items.Add(item);
        }
        return new Lr2FolderFileSyncItemsResult(items, hasReadFailures);
    }

    private static Lr2FolderFileSyncItem CreateLr2FolderFileSyncItem(string filePath)
    {
        try
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(filePath),
                Definition = Lr2FolderFileProjection.ParseDefinition(File.ReadLines(filePath, Encoding.GetEncoding("shift_jis")))
            };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                LastWriteTimeUtc = null,
                Definition = null
            };
        }
    }

    private sealed class Lr2FolderFileSyncItemsResult(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        bool hasReadFailures)
    {
        public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; } = items ?? [];

        public bool HasReadFailures { get; } = hasReadFailures;
    }

    private static SongRowBackfillResult UpsertSongRows(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<BMSFile> songRows,
        ISet<string> textFileDirectories)
    {
        if (songRows == null || songRows.Count == 0)
        {
            return new SongRowBackfillResult(0, 0, 0, 0, []);
        }

        var rowsToWrite = new List<BMSFile>();
        int parseFailureCount = 0;
        foreach (BMSFile song in songRows.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path)))
        {
            BMSFile row = CreateBackfillSongRow(song, textFileDirectories, out bool parsedFromSnapshot);
            if (row == null || string.IsNullOrWhiteSpace(row.path))
            {
                continue;
            }
            if (!parsedFromSnapshot)
            {
                parseFailureCount++;
            }
            rowsToWrite.Add(row);
        }
        if (rowsToWrite.Count == 0)
        {
            return new SongRowBackfillResult(0, parseFailureCount, 0, 0, []);
        }

        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        int chartInfoAppliedCount = ApplyCurrentChartInfoRows(songDb, rowsToWrite);
        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        int processed = 0;
        int compatibilityApplied = 0;
        var compatibilityInfos = new List<BMSFileMaintenanceInfo>();
        songDb.BeginTransaction();
        try
        {
            foreach (BMSFile song in rowsToWrite)
            {
                Lr2SongDbWriter.UpsertGeneratedSong(songDb, song);
                if (TryCreateLr2CompatibilityMaintenanceInfo(song, out BMSFileMaintenanceInfo compatibilityInfo))
                {
                    UpsertLr2CompatibilityFacts(songDb, compatibilityInfo);
                    compatibilityInfos.Add(compatibilityInfo);
                    compatibilityApplied++;
                }
                processed++;
            }
            songDb.Commit();
        }
        catch
        {
            songDb.Rollback();
            throw;
        }
        return new SongRowBackfillResult(processed, parseFailureCount, chartInfoAppliedCount, compatibilityApplied, compatibilityInfos);
    }

    private static BMSFile CreateBackfillSongRow(
        BMSFile existingSong,
        ISet<string> textFileDirectories,
        out bool parsedFromSnapshot)
    {
        parsedFromSnapshot = false;
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.path))
        {
            return null;
        }

        try
        {
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(existingSong.path);
            BMSFile.BmsEncodingDetectionResult detectionResult = BMSFile.DetectEncodingOfBMSFileDetailed(snapshot);
            string encodingName = ResolveSafeBackfillParseEncoding(detectionResult);
            if (string.IsNullOrWhiteSpace(encodingName))
            {
                return CreateFallbackBackfillSongRow(existingSong, textFileDirectories);
            }

            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot, encodingName);
            Lr2SongRowEnricher.EnrichParsedSong(
                parsed,
                snapshot,
                ResolveTextGroupFlag(existingSong.path, textFileDirectories, existingSong.txt.GetValueOrDefault()),
                existingSong);
            parsedFromSnapshot = true;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return CreateFallbackBackfillSongRow(existingSong, textFileDirectories);
        }
    }

    private static BMSFile CreateFallbackBackfillSongRow(BMSFile existingSong, ISet<string> textFileDirectories)
    {
        BMSFile copy = existingSong?.CreateSongRowPersistenceCopy();
        copy?.SetTextGroupFlag(ResolveTextGroupFlag(existingSong?.path, textFileDirectories, existingSong?.txt.GetValueOrDefault() ?? 0));
        return copy;
    }

    private static string ResolveSafeBackfillParseEncoding(BMSFile.BmsEncodingDetectionResult detectionResult)
    {
        if (detectionResult == null)
        {
            return null;
        }
        if (detectionResult.Outcome == BMSFile.EncodingDetectionOutcome.Unknown
            || detectionResult.Outcome == BMSFile.EncodingDetectionOutcome.Other)
        {
            return null;
        }

        string encodingName = detectionResult.EncodingName;
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return null;
        }
        return encodingName.TrimEnd('?');
    }

    private static int ResolveTextGroupFlag(string path, ISet<string> textFileDirectories, int fallback)
    {
        if (string.IsNullOrWhiteSpace(path) || textFileDirectories == null)
        {
            return fallback == 0 ? 0 : 1;
        }

        string directory;
        try
        {
            directory = Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return fallback == 0 ? 0 : 1;
        }
        if (string.IsNullOrWhiteSpace(directory))
        {
            return fallback == 0 ? 0 : 1;
        }
        return textFileDirectories.Contains(Path.GetFullPath(directory)) ? 1 : 0;
    }

    private static int ApplyCurrentChartInfoRows(LR2SongDBExtended songDb, IReadOnlyCollection<BMSFile> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return 0;
        }

        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        Dictionary<string, LR2SongDBExtended.chart_info> bySha256 = LoadCurrentChartInfoRowsByColumn(
            songDb,
            SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.sha256),
            rows.Select(row => row?.sha256));
        Dictionary<string, LR2SongDBExtended.chart_info> byMd5 = LoadCurrentChartInfoRowsByColumn(
            songDb,
            SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.md5),
            rows.Select(row => row?.hash),
            firstRowPerKey: true);

        int applied = 0;
        foreach (BMSFile row in rows)
        {
            LR2SongDBExtended.chart_info chartInfo = ResolveChartInfo(row, bySha256, byMd5);
            if (chartInfo == null)
            {
                continue;
            }
            if (!IsChartInfoCompatible(row, chartInfo))
            {
                continue;
            }

            row.ApplyLr2ChartInfoColumns(chartInfo);
            applied++;
        }
        return applied;
    }

    private static LR2SongDBExtended.chart_info ResolveChartInfo(
        BMSFile row,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> bySha256,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> byMd5)
    {
        if (row == null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(row.sha256)
            && bySha256 != null
            && bySha256.TryGetValue(row.sha256, out LR2SongDBExtended.chart_info bySha256Row))
        {
            return bySha256Row;
        }
        if (!string.IsNullOrWhiteSpace(row.hash)
            && byMd5 != null
            && byMd5.TryGetValue(row.hash, out LR2SongDBExtended.chart_info byMd5Row))
        {
            return byMd5Row;
        }
        return null;
    }

    private static bool IsChartInfoCompatible(BMSFile row, LR2SongDBExtended.chart_info chartInfo)
    {
        if (row == null || chartInfo == null)
        {
            return false;
        }
        return string.IsNullOrWhiteSpace(row.hash)
            || string.IsNullOrWhiteSpace(chartInfo.md5)
            || string.Equals(row.hash, chartInfo.md5, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, LR2SongDBExtended.chart_info> LoadCurrentChartInfoRowsByColumn(
        LR2SongDBExtended songDb,
        string columnName,
        IEnumerable<string> keys,
        bool firstRowPerKey = false)
    {
        List<string> normalizedKeys = [.. (keys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        var result = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        if (normalizedKeys.Count == 0)
        {
            return result;
        }

        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        string parserVersionColumn = SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.parser_version);
        const int chunkSize = 500;
        for (int offset = 0; offset < normalizedKeys.Count; offset += chunkSize)
        {
            List<string> chunk = normalizedKeys.Skip(offset).Take(chunkSize).ToList();
            string placeholders = string.Join(",", chunk.Select(_ => "?"));
            string sql = "SELECT * FROM " + tableName
                + " WHERE " + columnName + " IN (" + placeholders + ")"
                + " AND " + parserVersionColumn + " >= ?"
                + " ORDER BY " + columnName + " ASC, "
                + SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(row => row.sha256) + " ASC;";
            object[] parameters = [.. chunk.Cast<object>(), BmsLibraryDbGateway.CurrentChartInfoParserVersion];
            foreach (LR2SongDBExtended.chart_info row in songDb.Query<LR2SongDBExtended.chart_info>(sql, parameters))
            {
                string key = GetChartInfoLookupKey(row, columnName);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }
                if (firstRowPerKey && result.ContainsKey(key))
                {
                    continue;
                }
                result[key] = row;
            }
        }
        return result;
    }

    private static string GetChartInfoLookupKey(LR2SongDBExtended.chart_info row, string columnName)
    {
        if (row == null)
        {
            return null;
        }
        return string.Equals(columnName, SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(info => info.sha256), StringComparison.OrdinalIgnoreCase)
            ? row.sha256
            : row.md5;
    }

    private static void UpsertLr2CompatibilityFacts(LR2SongDBExtended songDb, BMSFileMaintenanceInfo info)
    {
        if (info == null)
        {
            return;
        }

        string tableName = SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName();
        int updated = songDb.Execute(
            "UPDATE " + tableName
            + " SET "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.hash) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_path_warning_flags) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_chart_path_cp932_bytes) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_folder_scan_cp932_bytes) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_warning_flags) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_max_raw_cp932_bytes) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_max_resolved_cp932_bytes) + " = ?, "
            + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_unsupported_count) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.path) + " = ?;",
            info.hash,
            info.lr2_path_warning_flags,
            info.lr2_chart_path_cp932_bytes,
            info.lr2_folder_scan_cp932_bytes,
            info.lr2_resource_warning_flags,
            info.lr2_resource_max_raw_cp932_bytes,
            info.lr2_resource_max_resolved_cp932_bytes,
            info.lr2_resource_unsupported_count,
            info.path);
        if (updated <= 0)
        {
            songDb.Insert(info, typeof(LR2SongDBExtended.maintenance));
        }
    }

    private static bool TryCreateLr2CompatibilityMaintenanceInfo(BMSFile row, out BMSFileMaintenanceInfo info)
    {
        info = null;
        if (row == null || string.IsNullOrWhiteSpace(row.path))
        {
            return false;
        }

        ChartFile chart = ChartFileProjection.FromBmsFile(
            row,
            includeWarningSnapshot: false,
            includeResourceReferences: true,
            includeScoreSnapshot: false);
        if (chart == null)
        {
            return false;
        }

        ChartResourceSnapshot resources = TryCreateChartResourceSnapshot(chart);
        Lr2ChartPathEvaluation pathEvaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(chart.Path);
        Lr2ResourceReferenceEvaluation resourceEvaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(chart.Path, resources);
        info = new BMSFileMaintenanceInfo
        {
            path = row.path,
            hash = row.hash,
            lr2_path_warning_flags = (int)pathEvaluation.WarningFlags,
            lr2_chart_path_cp932_bytes = pathEvaluation.ChartPathCp932Bytes,
            lr2_folder_scan_cp932_bytes = pathEvaluation.FolderScanPathCp932Bytes,
            lr2_resource_warning_flags = (int)resourceEvaluation.WarningFlags,
            lr2_resource_max_raw_cp932_bytes = resourceEvaluation.MaxRawCp932Bytes,
            lr2_resource_max_resolved_cp932_bytes = resourceEvaluation.MaxResolvedCp932Bytes,
            lr2_resource_unsupported_count = resourceEvaluation.UnsupportedCount
        };
        return true;
    }

    private static ChartResourceSnapshot TryCreateChartResourceSnapshot(ChartFile chart)
    {
        try
        {
            return ChartResourceSnapshot.Create(chart);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return null;
        }
    }

    private sealed class SongRowBackfillResult(
        int processedCount,
        int parseFailureCount,
        int chartInfoAppliedCount,
        int lr2CompatibilityAppliedCount,
        IReadOnlyList<BMSFileMaintenanceInfo> lr2CompatibilityMaintenanceInfos)
    {
        public int ProcessedCount { get; } = processedCount;

        public int ParseFailureCount { get; } = parseFailureCount;

        public int ChartInfoAppliedCount { get; } = chartInfoAppliedCount;

        public int Lr2CompatibilityAppliedCount { get; } = lr2CompatibilityAppliedCount;

        public IReadOnlyList<BMSFileMaintenanceInfo> Lr2CompatibilityMaintenanceInfos { get; } = lr2CompatibilityMaintenanceInfos ?? [];
    }

    private sealed class StartupDiagnosticSongRow
    {
        public string Path { get; set; }

        public int? Date { get; set; }
    }

    private sealed class StartupDiagnosticFolderRow
    {
        public string Path { get; set; }

        public int? Type { get; set; }

        public int? Date { get; set; }
    }
}
