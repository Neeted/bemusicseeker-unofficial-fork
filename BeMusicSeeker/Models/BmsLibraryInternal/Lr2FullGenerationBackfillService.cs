using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FullGenerationBackfillRequest
{
    public string Signature { get; set; }

    public string RunId { get; set; }

    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Lr2FolderFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Lr2FolderDiscoveryDirectories { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderPruneDirectories { get; set; } = [];

    public string Lr2RootPath { get; set; }

    public string Lr2RootCustomFolderOutputBaseDir { get; set; }

    public IReadOnlyCollection<string> Lr2BuiltinFolderSourceDirectories { get; set; } = [];

    public bool Lr2FolderFileDiscoveryComplete { get; set; }

    public IReadOnlyCollection<BMSFile> SongRows { get; set; } = [];

    public IReadOnlyCollection<string> TextFileDirectories { get; set; } = [];

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public CancellationToken CancellationToken { get; set; }

    public Func<bool> IsSourceCurrent { get; set; }

    public Action<Lr2FullGenerationBackfillProgress> ProgressReporter { get; set; }

    public Action<string> LogInstallPerformance { get; set; }
}

internal sealed class Lr2FullGenerationBackfillProgress
{
    public int ProcessedCursor { get; set; }

    public int TotalCount { get; set; }

    public string Stage { get; set; } = string.Empty;

    public int StageProcessedCount { get; set; }

    public int StageTotalCount { get; set; }
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

    public int StaleSongRowPrunedCount { get; set; }

    public IReadOnlyList<BMSFileMaintenanceInfo> Lr2CompatibilityMaintenanceInfos { get; set; } = [];

    public Lr2StartupScanDiagnosticResult StartupScanDiagnosticResult { get; set; }

    public long ElapsedMs { get; set; }
}

internal sealed class Lr2StartupScanDiagnosticResult(
    int noRootSetBlockerCount,
    int missingCurrentSongRowCount,
    int dateMissingSongRowCount,
    int unknownRootSongRowCount,
    int missingExpectedFolderRowCount,
    int missingExpectedLr2FolderRowCount,
    int dateMissingFolderRowCount,
    int dateStaleFolderRowCount,
    int unknownRootFolderRowCount,
    IReadOnlyList<string> cleanupFolderRowPaths,
    IReadOnlyList<Lr2StartupScanFolderDateUpdate> folderDateUpdates)
{
    public int NoRootSetBlockerCount { get; } = noRootSetBlockerCount;

    public int MissingCurrentSongRowCount { get; } = missingCurrentSongRowCount;

    public int DateMissingSongRowCount { get; } = dateMissingSongRowCount;

    public int UnknownRootSongRowCount { get; } = unknownRootSongRowCount;

    public int MissingExpectedFolderRowCount { get; } = missingExpectedFolderRowCount;

    public int MissingExpectedLr2FolderRowCount { get; } = missingExpectedLr2FolderRowCount;

    public int DateMissingFolderRowCount { get; } = dateMissingFolderRowCount;

    public int DateStaleFolderRowCount { get; } = dateStaleFolderRowCount;

    public int UnknownRootFolderRowCount { get; } = unknownRootFolderRowCount;

    public IReadOnlyList<string> CleanupFolderRowPaths { get; } = cleanupFolderRowPaths ?? [];

    public IReadOnlyList<Lr2StartupScanFolderDateUpdate> FolderDateUpdates { get; } = folderDateUpdates ?? [];

    public int CleanupFolderRowCount => CleanupFolderRowPaths.Count;

    public int FolderDateUpdateCount => FolderDateUpdates.Count;

    public int TotalBlockerCount => NoRootSetBlockerCount
        + MissingCurrentSongRowCount
        + DateMissingSongRowCount
        + UnknownRootSongRowCount
        + MissingExpectedFolderRowCount
        + MissingExpectedLr2FolderRowCount
        + DateMissingFolderRowCount
        + DateStaleFolderRowCount
        + UnknownRootFolderRowCount;

    public bool IsClean => TotalBlockerCount == 0;

    public string ToLogDetail()
    {
        return "startup_scan_blockers"
            + " noRootSet=" + NoRootSetBlockerCount
            + " missingSongRows=" + MissingCurrentSongRowCount
            + " dateMissingSongRows=" + DateMissingSongRowCount
            + " unknownRootSongRows=" + UnknownRootSongRowCount
            + " missingExpectedFolderRows=" + MissingExpectedFolderRowCount
            + " missingExpectedLr2FolderRows=" + MissingExpectedLr2FolderRowCount
            + " dateMissingFolderRows=" + DateMissingFolderRowCount
            + " dateStaleFolderRows=" + DateStaleFolderRowCount
            + " unknownRootFolderRows=" + UnknownRootFolderRowCount
            + " cleanupFolderRows=" + CleanupFolderRowCount
            + " folderDateUpdates=" + FolderDateUpdateCount;
    }
}

internal sealed class Lr2StartupScanFolderDateUpdate(string path, int date)
{
    public string Path { get; } = path ?? string.Empty;

    public int Date { get; } = date;
}

internal sealed class Lr2StartupScanFolderRepairResult(int deletedCount, int updatedDateCount)
{
    public int DeletedCount { get; } = deletedCount;

    public int UpdatedDateCount { get; } = updatedDateCount;
}

internal sealed class Lr2StartupScanBlockerCleanupResult(
    Lr2StartupScanDiagnosticResult diagnosticBefore,
    int deletedFolderRowCount,
    Lr2StartupScanDiagnosticResult diagnosticAfter)
{
    public Lr2StartupScanDiagnosticResult DiagnosticBefore { get; } = diagnosticBefore;

    public int DeletedFolderRowCount { get; } = deletedFolderRowCount;

    public Lr2StartupScanDiagnosticResult DiagnosticAfter { get; } = diagnosticAfter;

    public bool HasRemainingBlockers => DiagnosticAfter?.IsClean != true;
}

internal static class Lr2FullGenerationBackfillService
{
    internal const string CompletedStage = "completed";

    internal const string StartupScanBlockersStage = "startup_scan_blockers";

    internal const string StartupScanBlockersReason = "startup_scan_blockers_detected";

    internal const string SourceStaleStage = "source_stale";

    internal const string SourceStaleReason = "source_stale_detected";

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
        Dictionary<string, RootFileEnumerationEntry> directoryEntries = NormalizeEnumerationEntries(request.DirectoryEntries);
        Dictionary<string, RootFileEnumerationEntry> lr2FolderFileEntries = NormalizeEnumerationEntries(request.Lr2FolderFileEntries);
        List<string> lr2FolderFilePaths = [.. (request.Lr2FolderFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Concat(lr2FolderFileEntries.Keys)
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
        int normalFolderEndCursor = normalFolderTargetCount;
        int lr2FolderEndCursor = normalFolderEndCursor + lr2FolderFilePaths.Count;
        int songRowsEndCursor = totalCount;
        int resumeCursor = 0;
        if (Lr2FullGenerationStatusService.TryCreateResumeCandidate(songDb, request.Signature, totalCount, out Lr2FullGenerationResumeCandidate resumeCandidate))
        {
            resumeCursor = NormalizeResumeCursor(
                resumeCandidate.ProcessedCursor,
                normalFolderEndCursor,
                lr2FolderEndCursor,
                songRowsEndCursor);
        }
        string initialStage = ResolveInitialStage(resumeCursor, normalFolderEndCursor, lr2FolderEndCursor, songRowsEndCursor);

        Lr2FullGenerationStatusService.MarkRunning(
            songDb,
            request.Signature,
            request.RunId,
            totalCount,
            stage: initialStage,
            nowUtc: request.StartedAtUtc,
            processedCursor: resumeCursor);
        LogBackfill(request, "lr2_full_generation_backfill input_summary"
            + " roots=" + roots.Count
            + " charts=" + chartPaths.Count
            + " folderInfoCandidates=" + folderInfoFilePaths.Count
            + " lr2FolderCandidates=" + lr2FolderFilePaths.Count
            + " songRows=" + songRows.Count
            + " durableTotal=" + totalCount
            + " resumeCursor=" + resumeCursor
            + " initialStage=" + initialStage);
        ReportProgress(request, resumeCursor, totalCount, initialStage, ResolveStageProcessedCount(resumeCursor, normalFolderEndCursor, lr2FolderEndCursor), ResolveStageTotalCount(initialStage, normalFolderTargetCount, lr2FolderFilePaths.Count, songRows.Count));
        ThrowIfCancellationRequested(songDb, request, resumeCursor, totalCount, initialStage);

        Lr2NormalFolderDbSyncResult normalFolderResult = null;
        int normalFolderProcessedCount = resumeCursor >= normalFolderEndCursor
            ? normalFolderEndCursor
            : 0;
        if (resumeCursor < normalFolderEndCursor && roots.Count > 0)
        {
            LogStage(request, "stage_start", "normal_folders", normalFolderTargetCount, normalFolderProcessedCount, normalFolderProcessedCount);
            normalFolderResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
            {
                RootDirectories = roots,
                ChartPaths = chartPaths,
                FolderInfoFilePaths = folderInfoFilePaths,
                FolderInfoFileEntries = request.FolderInfoFileEntries,
                DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(directoryEntries),
                AllowPrune = true,
                GeneratedAtUtc = request.StartedAtUtc
            });
            normalFolderProcessedCount = normalFolderTargetCount;
            LogStage(request, "stage_done", "normal_folders", normalFolderTargetCount, normalFolderProcessedCount, normalFolderProcessedCount);
        }
        ThrowIfCancellationRequested(songDb, request, normalFolderProcessedCount, totalCount, "normal_folders_completed");

        if (resumeCursor < normalFolderEndCursor)
        {
            Lr2FullGenerationStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: normalFolderProcessedCount,
                totalCount: totalCount,
                stage: "normal_folders_completed",
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, normalFolderProcessedCount, totalCount, "normal_folders_completed", normalFolderTargetCount, normalFolderTargetCount);
        }

        if (resumeCursor < lr2FolderEndCursor)
        {
            LogStage(request, "stage_start", "lr2folder_files", lr2FolderFilePaths.Count, 0, normalFolderProcessedCount);
            Lr2FullGenerationStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: normalFolderProcessedCount,
                totalCount: totalCount,
                stage: "lr2folder_files",
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, normalFolderProcessedCount, totalCount, "lr2folder_files", 0, lr2FolderFilePaths.Count);
        }
        ThrowIfCancellationRequested(songDb, request, normalFolderProcessedCount, totalCount, "lr2folder_files");

        Lr2FolderFileDbSyncResult lr2FolderFileResult = null;
        int lr2FolderFileProcessedCount = resumeCursor >= lr2FolderEndCursor
            ? lr2FolderFilePaths.Count
            : 0;
        if (resumeCursor < lr2FolderEndCursor && lr2FolderDiscoveryDirectories.Count > 0)
        {
            Lr2FolderFileSyncItemsResult syncItems = CreateLr2FolderFileSyncItems(lr2FolderFilePaths, request, lr2FolderFileEntries);
            lr2FolderFileResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items = syncItems.Items,
                ScopeDirectories = lr2FolderPruneDirectories,
                DirectoryRowScopeDirectories = CreateLr2FolderDirectoryRowScopeDirectories(request),
                AllowPrune = lr2FolderPruneDirectories.Count > 0
                    && request.Lr2FolderFileDiscoveryComplete
                    && !syncItems.HasReadFailures,
                GeneratedAtUtc = request.StartedAtUtc
            });
            lr2FolderFileProcessedCount = lr2FolderFileResult.ItemCount;
        }
        int folderProcessedCount = normalFolderProcessedCount + lr2FolderFileProcessedCount;
        if (resumeCursor < lr2FolderEndCursor)
        {
            LogStage(request, "stage_done", "lr2folder_files", lr2FolderFilePaths.Count, lr2FolderFileProcessedCount, folderProcessedCount);
        }
        ThrowIfCancellationRequested(songDb, request, folderProcessedCount, totalCount, "lr2folder_files_completed");

        if (resumeCursor < lr2FolderEndCursor)
        {
            Lr2FullGenerationStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: folderProcessedCount,
                totalCount: totalCount,
                stage: "lr2folder_files_completed",
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, folderProcessedCount, totalCount, "lr2folder_files_completed", lr2FolderFileProcessedCount, lr2FolderFilePaths.Count);
        }

        if (resumeCursor < songRowsEndCursor)
        {
            int songStageStart = Math.Max(0, resumeCursor - lr2FolderEndCursor);
            int songStageProcessedCursor = folderProcessedCount + songStageStart;
            LogStage(request, "stage_start", "song_rows", songRows.Count, songStageStart, songStageProcessedCursor);
            Lr2FullGenerationStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: songStageProcessedCursor,
                totalCount: totalCount,
                stage: "song_rows",
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, songStageProcessedCursor, totalCount, "song_rows", songStageStart, songRows.Count);
        }
        ThrowIfCancellationRequested(songDb, request, Math.Max(folderProcessedCount, resumeCursor), totalCount, "song_rows");

        int songRowStartIndex = Math.Max(0, resumeCursor - lr2FolderEndCursor);
        SongRowBackfillResult songRowResult = resumeCursor >= songRowsEndCursor
            ? new SongRowBackfillResult(0, 0, 0, 0, [])
            : UpsertSongRows(
                songDb,
                songRows,
                textFileDirectories,
                songRowStartIndex,
                lr2FolderEndCursor,
                totalCount,
                request.Signature,
                request.RunId,
                request);
        int processedCount = resumeCursor >= songRowsEndCursor
            ? songRowsEndCursor
            : lr2FolderEndCursor + songRowStartIndex + songRowResult.ProcessedCount;
        ThrowIfCancellationRequested(songDb, request, processedCount, totalCount, "song_rows_completed");

        if (resumeCursor < songRowsEndCursor)
        {
            Lr2FullGenerationStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: processedCount,
                totalCount: totalCount,
                stage: "song_rows_completed",
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, processedCount, totalCount, "song_rows_completed", songRowStartIndex + songRowResult.ProcessedCount, songRows.Count);
            LogStage(request, "stage_done", "song_rows", songRows.Count, songRowStartIndex + songRowResult.ProcessedCount, processedCount);
        }

        Lr2SongPruneResult songPruneResult = new(0, songRows.Count);
        Lr2StartupScanDiagnosticResult diagnosticResult = null;
        string finalStage;
        string incompleteReason;
        if (!IsSourceCurrent(request))
        {
            Lr2FullGenerationStatusService.MarkIncomplete(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: processedCount,
                totalCount,
                stage: SourceStaleStage,
                detail: SourceStaleReason,
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, processedCount, totalCount, SourceStaleStage, 0, 0);
            finalStage = SourceStaleStage;
            incompleteReason = SourceStaleReason;
        }
        else
        {
            songPruneResult = Lr2SongDbWriter.DeleteSongsExceptCurrentPaths(
                songDb,
                songRows.Select(row => row?.path));
            if (songPruneResult.DeletedCount > 0)
            {
                LogBackfill(request, "lr2_full_generation_backfill song_row_prune"
                    + " currentPaths=" + songPruneResult.CurrentPathCount
                    + " deleted=" + songPruneResult.DeletedCount
                    + " processedCursor=" + processedCount);
            }

            diagnosticResult = DiagnoseStartupScanBlockers(
                songDb,
                roots,
                lr2FolderDiscoveryDirectories,
                songRows,
                request.Lr2RootPath,
                request);
            if (diagnosticResult.MissingExpectedFolderRowCount > 0 && roots.Count > 0)
            {
                Lr2NormalFolderDbSyncResult resyncResult = Lr2NormalFolderDbSyncService.Sync(songDb, new Lr2NormalFolderDbSyncRequest
                {
                    RootDirectories = roots,
                    ChartPaths = chartPaths,
                    FolderInfoFilePaths = folderInfoFilePaths,
                    FolderInfoFileEntries = request.FolderInfoFileEntries,
                    DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(directoryEntries),
                    AllowPrune = true,
                    GeneratedAtUtc = request.StartedAtUtc
                });
                LogBackfill(request, "lr2_full_generation_backfill startup_scan_blocker_resync"
                    + " stage=normal_folders"
                    + " missingExpectedFolderRows=" + diagnosticResult.MissingExpectedFolderRowCount
                    + " generated=" + resyncResult.GeneratedCount
                    + " upserted=" + resyncResult.UpsertedCount
                    + " deleted=" + resyncResult.DeletedCount
                    + " elapsedMs=" + resyncResult.ElapsedMs
                    + " processedCursor=" + processedCount);
                diagnosticResult = DiagnoseStartupScanBlockers(
                    songDb,
                    roots,
                    lr2FolderDiscoveryDirectories,
                    songRows,
                    request.Lr2RootPath,
                    request);
            }
            if (diagnosticResult.MissingExpectedLr2FolderRowCount > 0 && lr2FolderDiscoveryDirectories.Count > 0)
            {
                Lr2FolderFileSyncItemsResult syncItems = CreateLr2FolderFileSyncItems(lr2FolderFilePaths, request, lr2FolderFileEntries);
                Lr2FolderFileDbSyncResult resyncResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
                {
                    Items = syncItems.Items,
                    ScopeDirectories = lr2FolderPruneDirectories,
                    DirectoryRowScopeDirectories = CreateLr2FolderDirectoryRowScopeDirectories(request),
                    AllowPrune = lr2FolderPruneDirectories.Count > 0
                        && request.Lr2FolderFileDiscoveryComplete
                        && !syncItems.HasReadFailures,
                    GeneratedAtUtc = request.StartedAtUtc
                });
                LogBackfill(request, "lr2_full_generation_backfill startup_scan_blocker_resync"
                    + " stage=lr2folder_files"
                    + " missingExpectedLr2FolderRows=" + diagnosticResult.MissingExpectedLr2FolderRowCount
                    + " items=" + resyncResult.ItemCount
                    + " generated=" + resyncResult.GeneratedCount
                    + " upserted=" + resyncResult.UpsertedCount
                    + " deleted=" + resyncResult.DeletedCount
                    + " skippedMissingMetadata=" + resyncResult.SkippedMissingMetadataCount
                    + " elapsedMs=" + resyncResult.ElapsedMs
                    + " processedCursor=" + processedCount);
                diagnosticResult = DiagnoseStartupScanBlockers(
                    songDb,
                    roots,
                    lr2FolderDiscoveryDirectories,
                    songRows,
                    request.Lr2RootPath,
                    request);
            }
            if (diagnosticResult.CleanupFolderRowCount > 0 || diagnosticResult.FolderDateUpdateCount > 0)
            {
                Lr2StartupScanFolderRepairResult repairResult = ApplyStartupScanFolderRepairs(songDb, diagnosticResult);
                LogBackfill(request, "lr2_full_generation_backfill startup_scan_blocker_cleanup"
                    + " before=" + diagnosticResult.TotalBlockerCount
                    + " deletedFolderRows=" + repairResult.DeletedCount
                    + " updatedFolderDates=" + repairResult.UpdatedDateCount
                    + " cleanupFolderRows=" + diagnosticResult.CleanupFolderRowCount
                    + " folderDateUpdates=" + diagnosticResult.FolderDateUpdateCount
                    + " processedCursor=" + processedCount);
                diagnosticResult = DiagnoseStartupScanBlockers(
                    songDb,
                    roots,
                    lr2FolderDiscoveryDirectories,
                    songRows,
                    request.Lr2RootPath,
                    request);
            }
            if (!diagnosticResult.IsClean)
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
                LogBackfill(request, "lr2_full_generation_backfill startup_scan_blockers " + diagnosticResult.ToLogDetail()
                    + " total=" + diagnosticResult.TotalBlockerCount
                    + " cleanupFolderRows=" + diagnosticResult.CleanupFolderRowCount
                    + " processedCursor=" + processedCount);
                ReportProgress(request, processedCount, totalCount, StartupScanBlockersStage, 0, diagnosticResult.TotalBlockerCount);
                finalStage = StartupScanBlockersStage;
                incompleteReason = StartupScanBlockersReason;
            }
            else if (!IsSourceCurrent(request))
            {
                Lr2FullGenerationStatusService.MarkIncomplete(
                    songDb,
                    request.Signature,
                    request.RunId,
                    processedCursor: processedCount,
                    totalCount,
                    stage: SourceStaleStage,
                    detail: SourceStaleReason,
                    nowUtc: DateTime.UtcNow);
                ReportProgress(request, processedCount, totalCount, SourceStaleStage, 0, 0);
                finalStage = SourceStaleStage;
                incompleteReason = SourceStaleReason;
            }
            else
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    songDb,
                    request.Signature,
                    request.RunId,
                    totalCount,
                    nowUtc: DateTime.UtcNow);
                ReportProgress(request, totalCount, totalCount, CompletedStage, totalCount, totalCount);
                finalStage = CompletedStage;
                incompleteReason = null;
            }
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
            StaleSongRowPrunedCount = songPruneResult.DeletedCount,
            Lr2CompatibilityMaintenanceInfos = songRowResult.Lr2CompatibilityMaintenanceInfos,
            StartupScanDiagnosticResult = diagnosticResult,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static bool IsSourceCurrent(Lr2FullGenerationBackfillRequest request)
    {
        if (request?.IsSourceCurrent == null)
        {
            return true;
        }
        try
        {
            return request.IsSourceCurrent();
        }
        catch
        {
            return false;
        }
    }

    private static void ThrowIfCancellationRequested(
        LR2SongDBExtended songDb,
        Lr2FullGenerationBackfillRequest request,
        int processedCursor,
        int totalCount,
        string stage)
    {
        if (request?.CancellationToken.IsCancellationRequested != true)
        {
            return;
        }

        Lr2FullGenerationStatusService.MarkCancelled(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor,
            totalCount,
            stage,
            DateTime.UtcNow);
        ReportProgress(request, processedCursor, totalCount, stage ?? "cancelled", 0, 0);
        throw new OperationCanceledException(request.CancellationToken);
    }

    private static int NormalizeResumeCursor(int cursor, int normalFolderEndCursor, int lr2FolderEndCursor, int songRowsEndCursor)
    {
        if (cursor <= 0)
        {
            return 0;
        }
        int safeCursor = Math.Min(cursor, Math.Max(0, songRowsEndCursor));
        if (safeCursor < normalFolderEndCursor)
        {
            return 0;
        }
        if (safeCursor < lr2FolderEndCursor)
        {
            return normalFolderEndCursor;
        }
        return safeCursor;
    }

    private static string ResolveInitialStage(int resumeCursor, int normalFolderEndCursor, int lr2FolderEndCursor, int songRowsEndCursor)
    {
        if (resumeCursor >= songRowsEndCursor)
        {
            return "final_validation";
        }
        if (resumeCursor >= lr2FolderEndCursor)
        {
            return "song_rows";
        }
        if (resumeCursor >= normalFolderEndCursor)
        {
            return "lr2folder_files";
        }
        return "normal_folders";
    }

    private static int ResolveStageProcessedCount(int durableCursor, int normalFolderEndCursor, int lr2FolderEndCursor)
    {
        if (durableCursor >= lr2FolderEndCursor)
        {
            return Math.Max(0, durableCursor - lr2FolderEndCursor);
        }
        if (durableCursor >= normalFolderEndCursor)
        {
            return Math.Max(0, durableCursor - normalFolderEndCursor);
        }
        return Math.Max(0, durableCursor);
    }

    private static int ResolveStageTotalCount(string stage, int normalFolderTotalCount, int lr2FolderTotalCount, int songRowTotalCount)
    {
        return stage switch
        {
            "normal_folders" or "normal_folders_completed" => Math.Max(0, normalFolderTotalCount),
            "lr2folder_files" or "lr2folder_files_completed" => Math.Max(0, lr2FolderTotalCount),
            "song_rows" or "song_rows_completed" => Math.Max(0, songRowTotalCount),
            _ => 0,
        };
    }

    private static void ReportProgress(
        Lr2FullGenerationBackfillRequest request,
        int processedCount,
        int totalCount,
        string stage,
        int stageProcessedCount,
        int stageTotalCount)
    {
        try
        {
            request?.ProgressReporter?.Invoke(new Lr2FullGenerationBackfillProgress
            {
                ProcessedCursor = Math.Max(0, processedCount),
                TotalCount = Math.Max(0, totalCount),
                Stage = stage ?? string.Empty,
                StageProcessedCount = Math.Max(0, stageProcessedCount),
                StageTotalCount = Math.Max(0, stageTotalCount)
            });
        }
        catch
        {
            // Progress observation must not affect the durable backfill run.
        }
    }

    private static void LogStage(
        Lr2FullGenerationBackfillRequest request,
        string action,
        string stage,
        int totalCount,
        int processedCount,
        int processedCursor)
    {
        LogBackfill(request, "lr2_full_generation_backfill " + action
            + " stage=" + (stage ?? string.Empty)
            + " processed=" + Math.Max(0, processedCount)
            + " total=" + Math.Max(0, totalCount)
            + " processedCursor=" + Math.Max(0, processedCursor));
    }

    private static void LogBackfill(Lr2FullGenerationBackfillRequest request, string message)
    {
        try
        {
            request?.LogInstallPerformance?.Invoke(message);
        }
        catch
        {
            // Diagnostics must not affect durable backfill semantics.
        }
    }

    private static Lr2StartupScanDiagnosticResult DiagnoseStartupScanBlockers(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> rootDirectories,
        IReadOnlyCollection<string> lr2FolderDiscoveryDirectories,
        IReadOnlyCollection<BMSFile> currentSongRows,
        string lr2RootPath,
        Lr2FullGenerationBackfillRequest request = null)
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

        int noRootSetBlockerCount = 0;
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
        int dateStaleFolderRowCount = 0;
        int unknownRootFolderRowCount = 0;
        var cleanupFolderRowPaths = new HashSet<string>(StringComparer.Ordinal);
        var folderDateUpdates = new Dictionary<string, Lr2StartupScanFolderDateUpdate>(StringComparer.Ordinal);
        HashSet<string> expectedNormalFolderPaths = CreateExpectedNormalFolderRowPaths(roots, currentPaths);
        HashSet<string> expectedLr2FolderPaths = CreateExpectedLr2FolderRowPaths(request);
        var existingNormalFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingLr2FolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            string diagnosticPath = NormalizeFolderDiagnosticPath(row.Path, lr2RootPath);
            bool hasDiagnosticPath = !string.IsNullOrWhiteSpace(diagnosticPath);
            bool isLr2FolderFileRow = hasDiagnosticPath && IsLr2FolderDiagnosticPath(diagnosticPath);
            bool isNormalFolderRow = row.Type.GetValueOrDefault() == 1;
            bool isLegacyDirectoryRow = !isLr2FolderFileRow
                && (!row.Type.HasValue || row.Type.GetValueOrDefault() == 0);
            if (isNormalFolderRow)
            {
                string normalFolderPath = Lr2FolderPath.ToFolderPath(row.Path);
                if (!string.IsNullOrWhiteSpace(normalFolderPath))
                {
                    existingNormalFolderPaths.Add(normalFolderPath);
                }
            }
            if ((isNormalFolderRow || isLegacyDirectoryRow)
                && IsUnderAnyRoot(diagnosticPath, roots)
                && !expectedNormalFolderPaths.Contains(Lr2FolderPath.ToFolderPath(row.Path)))
            {
                AddCleanupFolderRowPath(cleanupFolderRowPaths, row.Path);
            }

            bool isLr2FolderScopedRow = hasDiagnosticPath && (isLr2FolderFileRow || IsUnderAnyRoot(diagnosticPath, lr2FolderRoots));

            if (!row.Date.HasValue || row.Date.GetValueOrDefault() <= 0)
            {
                dateMissingFolderRowCount++;
                if (hasDiagnosticPath
                    && ResolveFolderDiagnosticDate(isLr2FolderFileRow, row.Path, diagnosticPath, request, out int missingDateStatusDate) == FolderDiagnosticDateStatus.Resolved)
                {
                    AddFolderDateUpdate(folderDateUpdates, row.Path, missingDateStatusDate);
                }
                else
                {
                    AddCleanupFolderRowPath(cleanupFolderRowPaths, row.Path);
                }
            }

            if (isLr2FolderFileRow && IsExistingLr2FolderRowKind(row.Type))
            {
                string databasePath = Lr2FolderFileProjection.NormalizeDatabasePath(row.Path);
                if (!string.IsNullOrWhiteSpace(databasePath))
                {
                    existingLr2FolderPaths.Add(databasePath);
                    if (isLr2FolderScopedRow && !expectedLr2FolderPaths.Contains(databasePath))
                    {
                        AddCleanupFolderRowPath(cleanupFolderRowPaths, row.Path);
                    }
                }
            }

            if (row.Date.HasValue && row.Date.GetValueOrDefault() > 0)
            {
                int expectedDate = 0;
                FolderDiagnosticDateStatus dateStatus = hasDiagnosticPath
                    ? ResolveFolderDiagnosticDate(isLr2FolderFileRow, row.Path, diagnosticPath, request, out expectedDate)
                    : FolderDiagnosticDateStatus.Unavailable;
                if (dateStatus == FolderDiagnosticDateStatus.Resolved && expectedDate != row.Date.GetValueOrDefault())
                {
                    dateStaleFolderRowCount++;
                    AddFolderDateUpdate(folderDateUpdates, row.Path, expectedDate);
                }
            }

            IReadOnlyList<string> scopeRoots = isLr2FolderScopedRow ? allFolderRoots : roots;
            if (scopeRoots.Count > 0 && !IsUnderAnyRoot(diagnosticPath, scopeRoots))
            {
                unknownRootFolderRowCount++;
                AddCleanupFolderRowPath(cleanupFolderRowPaths, row.Path);
            }
        }
        int missingExpectedFolderRowCount = CountMissingExpectedNormalFolderRows(
            expectedNormalFolderPaths,
            existingNormalFolderPaths);
        int missingExpectedLr2FolderRowCount = CountMissingExpectedLr2FolderRows(
            expectedLr2FolderPaths,
            existingLr2FolderPaths);

        return new Lr2StartupScanDiagnosticResult(
            noRootSetBlockerCount,
            missingCurrentSongRowCount,
            dateMissingSongRowCount,
            unknownRootSongRowCount,
            missingExpectedFolderRowCount,
            missingExpectedLr2FolderRowCount,
            dateMissingFolderRowCount,
            dateStaleFolderRowCount,
            unknownRootFolderRowCount,
            [.. cleanupFolderRowPaths],
            [.. folderDateUpdates.Values]);
    }

    private static HashSet<string> CreateExpectedNormalFolderRowPaths(
        IReadOnlyCollection<string> rootDirectories,
        IEnumerable<string> currentChartPaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootDirectories == null || rootDirectories.Count == 0)
        {
            return result;
        }

        var compatibleChartPaths = new List<string>();
        foreach (string path in currentChartPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path)
                && Lr2CompatibilityEvaluator.EvaluateChartPath(path).CanComputeFolderParent)
            {
                compatibleChartPaths.Add(path);
            }
        }

        foreach (string directory in Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(rootDirectories, compatibleChartPaths))
        {
            string expectedPath = Lr2FolderPath.ToFolderPath(directory);
            if (!string.IsNullOrWhiteSpace(expectedPath))
            {
                result.Add(expectedPath);
            }
        }
        return result;
    }

    private static bool IsExistingLr2FolderRowKind(int? folderType)
    {
        if (!folderType.HasValue)
        {
            return true;
        }

        int type = folderType.GetValueOrDefault();
        return type == 0 || type == 2 || type == 3 || type == 4 || type == 6;
    }

    private static int CountMissingExpectedNormalFolderRows(
        IEnumerable<string> expectedNormalFolderPaths,
        ISet<string> existingNormalFolderPaths)
    {
        int missing = 0;
        foreach (string expectedPath in expectedNormalFolderPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(expectedPath)
                && existingNormalFolderPaths?.Contains(expectedPath) != true)
            {
                missing++;
            }
        }
        return missing;
    }

    private static HashSet<string> CreateExpectedLr2FolderRowPaths(Lr2FullGenerationBackfillRequest request)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request?.Lr2FolderFilePaths?.Count > 0 != true
            || request.Lr2FolderDiscoveryDirectories?.Count > 0 != true)
        {
            return result;
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath = NormalizeEnumerationEntries(request.Lr2FolderFileEntries);
        foreach (string filePath in request.Lr2FolderFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(filePath)
                || ResolveEnumerationEntry(entriesByPath, filePath)?.LastWriteTimeUtc == null)
            {
                continue;
            }

            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
            {
                FilePath = filePath,
                Lr2RootPath = request.Lr2RootPath,
                RootCustomFolderOutputBaseDir = request.Lr2RootCustomFolderOutputBaseDir,
                BuiltinSourceDirectories = request.Lr2BuiltinFolderSourceDirectories
            });
            if (classification.FolderType == 1)
            {
                continue;
            }

            string databasePath = Lr2FolderFileProjection.NormalizeDatabasePath(classification.DatabasePath ?? filePath);
            if (!string.IsNullOrWhiteSpace(databasePath))
            {
                result.Add(databasePath);
            }
        }
        return result;
    }

    private static int CountMissingExpectedLr2FolderRows(
        IEnumerable<string> expectedLr2FolderPaths,
        ISet<string> existingLr2FolderPaths)
    {
        int missing = 0;
        foreach (string expectedPath in expectedLr2FolderPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(expectedPath)
                && existingLr2FolderPaths?.Contains(expectedPath) != true)
            {
                missing++;
            }
        }
        return missing;
    }

    internal static Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> rootDirectories,
        IReadOnlyCollection<string> lr2FolderDiscoveryDirectories,
        IReadOnlyCollection<BMSFile> currentSongRows,
        string lr2RootPath)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        Lr2StartupScanDiagnosticResult before = DiagnoseStartupScanBlockers(
            songDb,
            rootDirectories,
            lr2FolderDiscoveryDirectories,
            currentSongRows,
            lr2RootPath);
        Lr2StartupScanFolderRepairResult repair = ApplyStartupScanFolderRepairs(songDb, before);

        Lr2StartupScanDiagnosticResult after = DiagnoseStartupScanBlockers(
            songDb,
            rootDirectories,
            lr2FolderDiscoveryDirectories,
            currentSongRows,
            lr2RootPath);
        return new Lr2StartupScanBlockerCleanupResult(before, repair.DeletedCount, after);
    }

    private static Lr2StartupScanFolderRepairResult ApplyStartupScanFolderRepairs(
        LR2SongDBExtended songDb,
        Lr2StartupScanDiagnosticResult diagnostic)
    {
        int deleted = 0;
        int updated = 0;
        if (diagnostic?.CleanupFolderRowPaths?.Count > 0)
        {
            Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(
                songDb,
                new Lr2FolderGenerationSyncPlan([], diagnostic.CleanupFolderRowPaths));
            deleted = writeResult.DeletedCount;
        }
        foreach (Lr2StartupScanFolderDateUpdate update in diagnostic?.FolderDateUpdates ?? [])
        {
            if (string.IsNullOrWhiteSpace(update?.Path) || update.Date <= 0)
            {
                continue;
            }

            updated += songDb.Execute(
                "UPDATE " + SQLiteTable<LR2SongDB.folder>.GetTableName()
                + " SET " + SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.date) + " = ? "
                + "WHERE " + SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path) + " = ?;",
                update.Date,
                update.Path);
        }
        return new Lr2StartupScanFolderRepairResult(deleted, updated);
    }

    private static void AddCleanupFolderRowPath(HashSet<string> paths, string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths?.Add(path);
        }
    }

    private static void AddFolderDateUpdate(
        IDictionary<string, Lr2StartupScanFolderDateUpdate> updates,
        string path,
        int date)
    {
        if (!string.IsNullOrWhiteSpace(path) && date > 0)
        {
            updates[path] = new Lr2StartupScanFolderDateUpdate(path, date);
        }
    }

    private static FolderDiagnosticDateStatus ResolveFolderDiagnosticDate(
        bool isLr2FolderFileRow,
        string rowPath,
        string diagnosticPath,
        Lr2FullGenerationBackfillRequest request,
        out int date)
    {
        date = 0;
        try
        {
            FolderDiagnosticDateStatus status = TryResolveFolderDiagnosticDateFromEnumeration(
                isLr2FolderFileRow,
                rowPath,
                diagnosticPath,
                request,
                out DateTime? lastWriteTimeUtc);
            if (status != FolderDiagnosticDateStatus.Resolved || lastWriteTimeUtc == null)
            {
                return status;
            }

            date = Lr2SongRowEnricher.ToLr2UnixSeconds(lastWriteTimeUtc.Value);
            return date > 0
                ? FolderDiagnosticDateStatus.Resolved
                : FolderDiagnosticDateStatus.Unavailable;
        }
        catch (IOException)
        {
            return FolderDiagnosticDateStatus.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderDiagnosticDateStatus.Unavailable;
        }
        catch (NotSupportedException)
        {
            return FolderDiagnosticDateStatus.Unavailable;
        }
        catch (ArgumentException)
        {
            return FolderDiagnosticDateStatus.Unavailable;
        }
    }

    private static FolderDiagnosticDateStatus TryResolveFolderDiagnosticDateFromEnumeration(
        bool isLr2FolderFileRow,
        string rowPath,
        string diagnosticPath,
        Lr2FullGenerationBackfillRequest request,
        out DateTime? lastWriteTimeUtc)
    {
        lastWriteTimeUtc = null;
        if (request == null)
        {
            return FolderDiagnosticDateStatus.Unavailable;
        }

        RootFileEnumerationEntry entry = isLr2FolderFileRow
            ? ResolveEnumerationEntry(request.Lr2FolderFileEntries, diagnosticPath)
                ?? ResolveEnumerationEntry(request.Lr2FolderFileEntries, rowPath)
            : ResolveEnumerationEntry(request.DirectoryEntries, diagnosticPath)
                ?? ResolveEnumerationEntry(request.DirectoryEntries, rowPath);
        if (entry?.LastWriteTimeUtc == null)
        {
            return FolderDiagnosticDateStatus.Unavailable;
        }

        lastWriteTimeUtc = entry.LastWriteTimeUtc.Value;
        return FolderDiagnosticDateStatus.Resolved;
    }

    private static bool IsLr2FolderDiagnosticPath(string diagnosticPath)
    {
        return !string.IsNullOrWhiteSpace(diagnosticPath)
            && string.Equals(Path.GetExtension(diagnosticPath), ".lr2folder", StringComparison.OrdinalIgnoreCase);
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

    private static string NormalizeFolderDiagnosticPath(string path, string lr2RootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            string relativeLr2FolderPath = Lr2FolderFileProjection.NormalizeKnownRelativeLr2FolderPath(path);
            if (!string.IsNullOrWhiteSpace(relativeLr2FolderPath) && !string.IsNullOrWhiteSpace(lr2RootPath))
            {
                return Path.GetFullPath(Path.Combine(lr2RootPath, relativeLr2FolderPath));
            }

            return string.Equals(Path.GetExtension(path), ".lr2folder", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path)
                : Lr2FolderPath.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static Lr2FolderFileSyncItemsResult CreateLr2FolderFileSyncItems(
        IEnumerable<string> filePaths,
        Lr2FullGenerationBackfillRequest request,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath = null)
    {
        var items = new List<Lr2FolderFileSyncItem>();
        bool hasReadFailures = false;
        foreach (string filePath in filePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            RootFileEnumerationEntry entry = ResolveEnumerationEntry(entriesByPath, filePath);
            Lr2FolderFileSyncItem item = CreateLr2FolderFileSyncItem(filePath, request, entry);
            if (item.LastWriteTimeUtc == null)
            {
                hasReadFailures = true;
            }
            items.Add(item);
        }
        return new Lr2FolderFileSyncItemsResult(items, hasReadFailures);
    }

    private static IReadOnlyCollection<string> CreateLr2FolderDirectoryRowScopeDirectories(Lr2FullGenerationBackfillRequest request)
    {
        var candidates = new List<string>();
        HashSet<string> excludedAbsoluteDirectories = [.. (request?.RootDirectories ?? [])
            .Concat(request?.Lr2BuiltinFolderSourceDirectories ?? [])
            .Select(NormalizeDirectoryPathOrNull)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        foreach (string directory in request?.Lr2FolderPruneDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
            {
                continue;
            }
            string normalized = NormalizeDirectoryPathOrNull(directory);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !excludedAbsoluteDirectories.Contains(normalized))
            {
                candidates.Add(normalized);
            }
        }
        if (request?.Lr2BuiltinFolderSourceDirectories?.Count > 0)
        {
            candidates.Add(@"LR2files\CustomFolder");
        }
        return [.. candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static Lr2FolderFileSyncItem CreateLr2FolderFileSyncItem(
        string filePath,
        Lr2FullGenerationBackfillRequest request,
        RootFileEnumerationEntry enumerationEntry = null)
    {
        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = request?.Lr2RootPath,
            RootCustomFolderOutputBaseDir = request?.Lr2RootCustomFolderOutputBaseDir,
            BuiltinSourceDirectories = request?.Lr2BuiltinFolderSourceDirectories
        });
        try
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                DatabasePath = classification.DatabasePath,
                LastWriteTimeUtc = ResolveLastWriteTimeUtc(filePath, enumerationEntry),
                Definition = Lr2FolderFileProjection.ParseDefinition(File.ReadLines(filePath, Encoding.GetEncoding("shift_jis"))),
                FolderType = classification.FolderType,
                ParentHash = classification.ParentHash
            };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                DatabasePath = classification.DatabasePath,
                LastWriteTimeUtc = null,
                Definition = null,
                FolderType = classification.FolderType,
                ParentHash = classification.ParentHash
            };
        }
    }

    private static DateTime? ResolveLastWriteTimeUtc(string filePath, RootFileEnumerationEntry enumerationEntry)
    {
        if (enumerationEntry?.LastWriteTimeUtc != null)
        {
            return enumerationEntry.LastWriteTimeUtc.Value;
        }
        return null;
    }

    private static RootFileEnumerationEntry ResolveEnumerationEntry(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath,
        string filePath)
    {
        return !string.IsNullOrWhiteSpace(filePath)
            && entriesByPath?.TryGetValue(filePath, out RootFileEnumerationEntry entry) == true
                ? entry
                : null;
    }

    private static Func<string, DateTime?> CreateLastWriteTimeResolver(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        if (entriesByPath == null || entriesByPath.Count == 0)
        {
            return null;
        }

        return path => ResolveEnumerationEntry(entriesByPath, path)?.LastWriteTimeUtc;
    }

    private static Dictionary<string, RootFileEnumerationEntry> NormalizeEnumerationEntries(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in entries ?? new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase))
        {
            RootFileEnumerationEntry entry = pair.Value;
            string path = !string.IsNullOrWhiteSpace(entry?.Path) ? entry.Path : pair.Key;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            result[path] = entry ?? new RootFileEnumerationEntry(path);
        }
        return result;
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
        ISet<string> textFileDirectories,
        int startIndex,
        int baseProcessedCursor,
        int totalCount,
        string signature,
        string runId,
        Lr2FullGenerationBackfillRequest request)
    {
        if (songRows == null || songRows.Count == 0)
        {
            return new SongRowBackfillResult(0, 0, 0, 0, []);
        }

        List<BMSFile> targetRows = [.. songRows.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        int safeStartIndex = Math.Max(0, startIndex);
        if (targetRows.Count == 0 || safeStartIndex >= targetRows.Count)
        {
            return new SongRowBackfillResult(0, 0, 0, 0, []);
        }

        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        const int songRowBackfillChunkSize = 500;
        int workerDegree = ResolveSongRowBackfillWorkerDegree();
        int readQueueCapacity = Math.Max(workerDegree * 2, Math.Min(songRowBackfillChunkSize * 2, Math.Max(songRowBackfillChunkSize, workerDegree * 16)));
        int computedQueueCapacity = Math.Max(songRowBackfillChunkSize, workerDegree * 16);
        int processed = 0;
        int parseFailureCount = 0;
        int chartInfoAppliedCount = 0;
        int compatibilityApplied = 0;
        var compatibilityInfos = new List<BMSFileMaintenanceInfo>();
        using var readQueue = new BlockingCollection<SongRowBackfillReadCandidate>(readQueueCapacity);
        using var computedQueue = new BlockingCollection<SongRowBackfillComputedItem>(computedQueueCapacity);
        long readerOutputWaitTicks = 0L;
        long workerOutputWaitTicks = 0L;
        int readQueueHighWatermark = 0;
        int computedQueueHighWatermark = 0;
        int writerFailed = 0;
        Exception pipelineException = null;
        CancellationToken cancellationToken = request?.CancellationToken ?? CancellationToken.None;
        LogBackfill(request, "lr2_full_generation_backfill pipeline_start"
            + " stage=song_rows"
            + " startIndex=" + safeStartIndex
            + " targetCount=" + targetRows.Count
            + " chunkSize=" + songRowBackfillChunkSize
            + " workerDegree=" + workerDegree
            + " readQueueCapacity=" + readQueueCapacity
            + " computedQueueCapacity=" + computedQueueCapacity);
        ThrowIfCancellationRequested(
            songDb,
            request,
            baseProcessedCursor + safeStartIndex,
            totalCount,
            "song_rows");

        void FlushSongRowChunk(List<SongRowBackfillComputedItem> chunk)
        {
            if (chunk == null || chunk.Count == 0)
            {
                return;
            }

            int offset = chunk[0].Index;
            ThrowIfCancellationRequested(
                songDb,
                request,
                baseProcessedCursor + offset,
                totalCount,
                "song_rows");
            LogBackfill(request, "lr2_full_generation_backfill chunk_start"
                + " stage=song_rows"
                + " offset=" + offset
                + " count=" + chunk.Count
                + " processedCursor=" + (baseProcessedCursor + offset));
            var rowsToWrite = new List<BMSFile>(chunk.Count);
            long chunkReadTicks = 0L;
            long chunkParseTicks = 0L;
            int chunkFallbackCount = 0;
            int chunkParseFailureCount = 0;
            foreach (SongRowBackfillComputedItem item in chunk)
            {
                chunkReadTicks += item.ReadElapsedTicks;
                chunkParseTicks += item.ParseElapsedTicks;
                BMSFile row = item.Row;
                if (row == null || string.IsNullOrWhiteSpace(row.path))
                {
                    continue;
                }
                if (!item.ParsedFromSnapshot)
                {
                    chunkParseFailureCount++;
                    chunkFallbackCount++;
                }
                rowsToWrite.Add(row);
            }

            var stopwatchChartInfo = Stopwatch.StartNew();
            int chunkChartInfoAppliedCount = ApplyCurrentChartInfoRows(songDb, rowsToWrite);
            stopwatchChartInfo.Stop();
            var stopwatchCompatibility = Stopwatch.StartNew();
            var chunkCompatibilityInfos = new List<BMSFileMaintenanceInfo>();
            foreach (BMSFile song in rowsToWrite)
            {
                if (TryCreateLr2CompatibilityMaintenanceInfo(song, out BMSFileMaintenanceInfo compatibilityInfo))
                {
                    chunkCompatibilityInfos.Add(compatibilityInfo);
                }
            }
            stopwatchCompatibility.Stop();
            var stopwatchCommit = Stopwatch.StartNew();
            songDb.BeginTransaction();
            try
            {
                foreach (BMSFile song in rowsToWrite)
                {
                    Lr2SongDbWriter.UpsertGeneratedSong(songDb, song);
                }
                foreach (BMSFileMaintenanceInfo compatibilityInfo in chunkCompatibilityInfos)
                {
                    UpsertLr2CompatibilityFacts(songDb, compatibilityInfo);
                }
                songDb.Commit();
                stopwatchCommit.Stop();
                compatibilityInfos.AddRange(chunkCompatibilityInfos);
                compatibilityApplied += chunkCompatibilityInfos.Count;
                parseFailureCount += chunkParseFailureCount;
                chartInfoAppliedCount += chunkChartInfoAppliedCount;
                processed += chunk.Count;
                int processedCursor = baseProcessedCursor + offset + chunk.Count;
                Lr2FullGenerationStatusService.UpdateCursor(
                    songDb,
                    signature,
                    runId,
                    processedCursor,
                    totalCount,
                    stage: "song_rows",
                    nowUtc: DateTime.UtcNow);
                ReportProgress(request, processedCursor, totalCount, "song_rows", offset + chunk.Count, targetRows.Count);
                LogBackfill(request, "lr2_full_generation_backfill chunk_done"
                    + " stage=song_rows"
                    + " offset=" + offset
                    + " count=" + chunk.Count
                    + " readMs=" + TicksToMilliseconds(chunkReadTicks)
                    + " parseMs=" + TicksToMilliseconds(chunkParseTicks)
                    + " chartInfoApplyMs=" + stopwatchChartInfo.ElapsedMilliseconds
                    + " compatibilityBuildMs=" + stopwatchCompatibility.ElapsedMilliseconds
                    + " commitMs=" + stopwatchCommit.ElapsedMilliseconds
                    + " fallbackCount=" + chunkFallbackCount
                    + " parseFailureCount=" + chunkParseFailureCount
                    + " compatibilityApplied=" + chunkCompatibilityInfos.Count
                    + " processedCursor=" + processedCursor);
            }
            catch (Exception ex)
            {
                stopwatchCommit.Stop();
                Exception rollbackException = TryRollbackSongRowChunk(songDb);
                Lr2FullGenerationStatusService.MarkFailed(
                    songDb,
                    signature,
                    runId,
                    processedCursor: baseProcessedCursor + offset,
                    totalCount,
                    stage: "song_rows",
                    error: rollbackException == null
                        ? ex.Message
                        : ex.Message + " rollback=" + rollbackException.Message,
                    nowUtc: DateTime.UtcNow);
                throw;
            }
        }

        Task readerTask = Task.Run(delegate
        {
            try
            {
                for (int index = safeStartIndex; index < targetRows.Count; index++)
                {
                    if (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                    SongRowBackfillReadCandidate candidate = ReadBackfillSongRowCandidate(index, targetRows[index]);
                    try
                    {
                        AddWithWait(readQueue, candidate, ref readerOutputWaitTicks, cancellationToken);
                        UpdateHighWatermark(ref readQueueHighWatermark, readQueue.Count);
                    }
                    catch (InvalidOperationException) when (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                }
            }
            finally
            {
                readQueue.CompleteAdding();
            }
        });

        Task[] workerTasks = [.. Enumerable.Range(0, workerDegree)
            .Select(_ => Task.Run(delegate
            {
                foreach (SongRowBackfillReadCandidate candidate in readQueue.GetConsumingEnumerable(cancellationToken))
                {
                    if (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                    SongRowBackfillComputedItem item = CreateBackfillSongRowItem(candidate, textFileDirectories);
                    try
                    {
                        AddWithWait(computedQueue, item, ref workerOutputWaitTicks, cancellationToken);
                        UpdateHighWatermark(ref computedQueueHighWatermark, computedQueue.Count);
                    }
                    catch (InvalidOperationException) when (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                }
            }))];
        Task workerCompletionTask = Task.WhenAll(workerTasks).ContinueWith(_ => TryCompleteAdding(computedQueue));
        var pendingItems = new SortedDictionary<int, SongRowBackfillComputedItem>();
        var writerChunk = new List<SongRowBackfillComputedItem>(songRowBackfillChunkSize);
        int nextIndexToCommit = safeStartIndex;
        try
        {
            foreach (SongRowBackfillComputedItem item in computedQueue.GetConsumingEnumerable(cancellationToken))
            {
                pendingItems[item.Index] = item;
                while (pendingItems.TryGetValue(nextIndexToCommit, out SongRowBackfillComputedItem nextItem))
                {
                    pendingItems.Remove(nextIndexToCommit);
                    writerChunk.Add(nextItem);
                    nextIndexToCommit++;
                    if (writerChunk.Count >= songRowBackfillChunkSize)
                    {
                        FlushSongRowChunk(writerChunk);
                        writerChunk = new List<SongRowBackfillComputedItem>(songRowBackfillChunkSize);
                    }
                }
            }
            Task.WaitAll(workerTasks);
            workerCompletionTask.Wait();
            FlushSongRowChunk(writerChunk);
        }
        catch (Exception ex)
        {
            pipelineException = UnwrapPipelineException(ex);
            Volatile.Write(ref writerFailed, 1);
            TryCompleteAdding(readQueue);
            TryCompleteAdding(computedQueue);
        }

        try
        {
            readerTask.Wait();
        }
        catch (AggregateException ex)
        {
            pipelineException ??= ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
        }
        try
        {
            Task.WaitAll(workerTasks);
            workerCompletionTask.Wait();
        }
        catch (AggregateException ex)
        {
            pipelineException ??= ex.Flatten().InnerExceptions.FirstOrDefault() ?? ex;
        }

        if (pipelineException != null)
        {
            if (pipelineException is OperationCanceledException)
            {
                ThrowIfCancellationRequested(
                    songDb,
                    request,
                    baseProcessedCursor + safeStartIndex + processed,
                    totalCount,
                    "song_rows");
            }
            Lr2FullGenerationStatusService.MarkFailed(
                songDb,
                signature,
                runId,
                processedCursor: baseProcessedCursor + safeStartIndex + processed,
                totalCount,
                stage: "song_rows",
                error: pipelineException.Message,
                nowUtc: DateTime.UtcNow);
            throw pipelineException;
        }

        LogBackfill(request, "lr2_full_generation_backfill pipeline_done"
            + " stage=song_rows"
            + " processed=" + processed
            + " parseFailureCount=" + parseFailureCount
            + " chartInfoApplied=" + chartInfoAppliedCount
            + " compatibilityApplied=" + compatibilityApplied
            + " readerOutputWaitMs=" + TicksToMilliseconds(readerOutputWaitTicks)
            + " workerOutputWaitMs=" + TicksToMilliseconds(workerOutputWaitTicks)
            + " readQueueHighWatermark=" + readQueueHighWatermark
            + " computedQueueHighWatermark=" + computedQueueHighWatermark);
        return new SongRowBackfillResult(processed, parseFailureCount, chartInfoAppliedCount, compatibilityApplied, compatibilityInfos);
    }

    private static void AddWithWait<T>(BlockingCollection<T> queue, T item, ref long waitTicks, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        queue.Add(item, cancellationToken);
        Interlocked.Add(ref waitTicks, Stopwatch.GetTimestamp() - start);
    }

    private static void UpdateHighWatermark(ref int highWatermark, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref highWatermark))
            && Interlocked.CompareExchange(ref highWatermark, value, current) != current)
        {
        }
    }

    private static void TryCompleteAdding<T>(BlockingCollection<T> queue)
    {
        try
        {
            queue?.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int ResolveSongRowBackfillWorkerDegree()
    {
        return Math.Max(1, Environment.ProcessorCount - 1);
    }

    private static Exception UnwrapPipelineException(Exception ex)
    {
        return ex is AggregateException aggregateException
            ? aggregateException.Flatten().InnerExceptions.FirstOrDefault() ?? aggregateException
            : ex;
    }

    private static Exception TryRollbackSongRowChunk(LR2SongDBExtended songDb)
    {
        try
        {
            songDb.Rollback();
            return null;
        }
        catch (Exception rollbackEx)
        {
            try
            {
                songDb.Execute("ROLLBACK;");
                return rollbackEx;
            }
            catch (Exception directRollbackEx)
            {
                return new AggregateException(rollbackEx, directRollbackEx);
            }
        }
    }

    private static SongRowBackfillReadCandidate ReadBackfillSongRowCandidate(int index, BMSFile existingSong)
    {
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.path))
        {
            return new SongRowBackfillReadCandidate(index, existingSong, null, 0L);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(existingSong.path);
            stopwatch.Stop();
            return new SongRowBackfillReadCandidate(index, existingSong, snapshot, stopwatch.ElapsedTicks);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return new SongRowBackfillReadCandidate(index, existingSong, null, 0L);
        }
    }

    private static SongRowBackfillComputedItem CreateBackfillSongRowItem(
        SongRowBackfillReadCandidate candidate,
        ISet<string> textFileDirectories)
    {
        BMSFile row = CreateBackfillSongRow(
            candidate,
            textFileDirectories,
            out bool parsedFromSnapshot,
            out long parseTicks);
        return new SongRowBackfillComputedItem(candidate.Index, row, parsedFromSnapshot, candidate.ReadElapsedTicks, parseTicks);
    }

    private static BMSFile CreateBackfillSongRow(
        SongRowBackfillReadCandidate candidate,
        ISet<string> textFileDirectories,
        out bool parsedFromSnapshot,
        out long parseTicks)
    {
        parsedFromSnapshot = false;
        parseTicks = 0L;
        BMSFile existingSong = candidate?.ExistingSong;
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.path))
        {
            return null;
        }
        if (candidate.Snapshot == null)
        {
            return CreateFallbackBackfillSongRow(existingSong, textFileDirectories);
        }

        try
        {
            var stopwatchParse = Stopwatch.StartNew();
            BMSFile.BmsEncodingDetectionResult detectionResult = BMSFile.DetectEncodingOfBMSFileDetailed(candidate.Snapshot);
            string encodingName = ResolveSafeBackfillParseEncoding(detectionResult);
            if (string.IsNullOrWhiteSpace(encodingName))
            {
                stopwatchParse.Stop();
                parseTicks = stopwatchParse.ElapsedTicks;
                return CreateFallbackBackfillSongRow(existingSong, textFileDirectories);
            }

            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(candidate.Snapshot, encodingName);
            Lr2SongRowEnricher.EnrichParsedSong(
                parsed,
                candidate.Snapshot,
                ResolveTextGroupFlag(existingSong.path, textFileDirectories, existingSong.txt.GetValueOrDefault()),
                existingSong);
            stopwatchParse.Stop();
            parseTicks = stopwatchParse.ElapsedTicks;
            parsedFromSnapshot = true;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return CreateFallbackBackfillSongRow(existingSong, textFileDirectories);
        }
    }

    private static long TicksToMilliseconds(long ticks)
    {
        return ticks <= 0L ? 0L : (long)(ticks * 1000.0 / Stopwatch.Frequency);
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

    private sealed class SongRowBackfillReadCandidate(
        int index,
        BMSFile existingSong,
        ChartFileSnapshot snapshot,
        long readElapsedTicks)
    {
        public int Index { get; } = index;

        public BMSFile ExistingSong { get; } = existingSong;

        public ChartFileSnapshot Snapshot { get; } = snapshot;

        public long ReadElapsedTicks { get; } = readElapsedTicks;
    }

    private sealed class SongRowBackfillComputedItem(
        int index,
        BMSFile row,
        bool parsedFromSnapshot,
        long readElapsedTicks,
        long parseElapsedTicks)
    {
        public int Index { get; } = index;

        public BMSFile Row { get; } = row;

        public bool ParsedFromSnapshot { get; } = parsedFromSnapshot;

        public long ReadElapsedTicks { get; } = readElapsedTicks;

        public long ParseElapsedTicks { get; } = parseElapsedTicks;
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

    private enum FolderDiagnosticDateStatus
    {
        Unavailable,
        Resolved
    }
}
