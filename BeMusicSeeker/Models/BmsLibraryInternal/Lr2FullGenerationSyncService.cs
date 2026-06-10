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

internal sealed class Lr2FullGenerationSyncRequest
{
    public string Signature { get; set; }

    public string RunId { get; set; }

    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> NormalFolderDirectoryPaths { get; set; } = [];

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

    public string Lr2NormalCustomFolderOutputBaseDir { get; set; }

    public string Lr2RootCustomFolderOutputBaseDir { get; set; }

    public IReadOnlyCollection<string> Lr2BuiltinFolderSourceDirectories { get; set; } = [];

    public bool Lr2FolderFileDiscoveryComplete { get; set; }

    public IReadOnlyCollection<BMSFile> SongRows { get; set; } = [];

    public IReadOnlyCollection<string> TextFileDirectories { get; set; } = [];

    public Func<BMSFile, LR2SongDBExtended.chart_info> ChartInfoResolver { get; set; }

    public bool ChartInfoResolverIsThreadSafe { get; set; }

    public TimeSpan? ChartInfoParseTimeout { get; set; }

    public ISet<string> CurrentChartInfoParseFailureMd5s { get; set; }

    public Action<IReadOnlyList<LR2SongDBExtended.chart_info>> ChartInfoRowsCommitted { get; set; }

    public Action<int, int> ChartInfoParseFailuresCommitted { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public CancellationToken CancellationToken { get; set; }

    public Func<bool> IsSourceCurrent { get; set; }

    public Action<Lr2FullGenerationSyncProgress> ProgressReporter { get; set; }

    public Action<IReadOnlyList<BMSFileMaintenanceInfo>> Lr2CompatibilityFactsCommitted { get; set; }

    public ISet<string> TransientSongRowsSkipPaths { get; set; }

    public Func<LR2SongDBExtended, IReadOnlyList<BMSFile>, Lr2FullGenerationSongRowsSkipVerificationResult> SongRowsSkipVerifier { get; set; }

    public Action<string> LogInstallPerformance { get; set; }
}

internal sealed class Lr2FullGenerationSyncProgress
{
    public int ProcessedCursor { get; set; }

    public int TotalCount { get; set; }

    public string Stage { get; set; } = string.Empty;

    public int StageProcessedCount { get; set; }

    public int StageTotalCount { get; set; }
}

internal sealed class Lr2FullGenerationSyncResult
{
    public int TotalCount { get; set; }

    public int ProcessedCount { get; set; }

    public string FinalStage { get; set; }

    public string IncompleteReason { get; set; }

    public Lr2NormalFolderDbSyncResult NormalFolderSyncResult { get; set; }

    public Lr2FolderFileDbSyncResult Lr2FolderFileSyncResult { get; set; }

    public int Lr2FolderFileProcessedCount { get; set; }

    public int SongRowProcessedCount { get; set; }

    public int SongRowSkippedCount { get; set; }

    public int SongRowParseFailureCount { get; set; }

    public int SongRowChartInfoAppliedCount { get; set; }

    public int SongRowLr2CompatibilityAppliedCount { get; set; }

    public int StaleSongRowPrunedCount { get; set; }

    public Lr2StartupScanDiagnosticResult StartupScanDiagnosticResult { get; set; }

    public long ElapsedMs { get; set; }
}

internal sealed class Lr2FullGenerationSongRowsSkipVerificationResult
{
    public bool CanSkip { get; set; }

    public string Reason { get; set; }

    public int TargetRows { get; set; }

    public int VerifiedRows { get; set; }

    public int MissingRows { get; set; }

    public int MismatchedRows { get; set; }

    public int DuplicatePathRows { get; set; }

    public int DigestCheckedRows { get; set; }

    public int DigestMissingRows { get; set; }

    public int DigestMismatchedRows { get; set; }

    public long ProjectionMs { get; set; }

    public long ExistingReadMs { get; set; }

    public long DigestReadMs { get; set; }

    public long ElapsedMs { get; set; }

    public IReadOnlyList<string> DiagnosticSamples { get; set; } = [];
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
    IReadOnlyList<string> dateMissingSongRowSamples,
    IReadOnlyList<string> missingExpectedFolderRowSamples,
    IReadOnlyList<string> missingExpectedLr2FolderRowSamples,
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

    public IReadOnlyList<string> DateMissingSongRowSamples { get; } = dateMissingSongRowSamples ?? [];

    public IReadOnlyList<string> MissingExpectedFolderRowSamples { get; } = missingExpectedFolderRowSamples ?? [];

    public IReadOnlyList<string> MissingExpectedLr2FolderRowSamples { get; } = missingExpectedLr2FolderRowSamples ?? [];

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

    public IEnumerable<string> EnumerateSampleLogDetails()
    {
        foreach (string path in DateMissingSongRowSamples)
        {
            yield return "kind=date_missing_song path=" + path;
        }
        foreach (string path in MissingExpectedFolderRowSamples)
        {
            yield return "kind=missing_expected_folder path=" + path;
        }
        foreach (string path in MissingExpectedLr2FolderRowSamples)
        {
            yield return "kind=missing_expected_lr2folder path=" + path;
        }
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

internal static class Lr2FullGenerationSyncService
{
    private const int SongRowSyncMaxWorkerDegree = 6;

    private const string TempLr2CompatibilityMaintenanceTable = "lr2_full_generation_compatibility_maintenance";

    private const string TempLr2CompatibilityMaintenanceMatchTable = "lr2_full_generation_compatibility_maintenance_match";

    private const int MaxPersistedChartInfoParseFailureMessageLength = 1024;

    internal const string CompletedStage = "completed";

    internal const string StartupScanBlockersStage = "startup_scan_blockers";

    internal const string StartupScanBlockersReason = "startup_scan_blockers_detected";

    internal const string SourceStaleStage = "source_stale";

    internal const string SourceStaleReason = "source_stale_detected";

    internal static Lr2FullGenerationSyncResult Run(
        LR2SongDBExtended songDb,
        Lr2FullGenerationSyncRequest request)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2FullGenerationSyncRequest();
        var stopwatch = Stopwatch.StartNew();
        List<string> roots = [.. (request.RootDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> chartPaths = [.. (request.ChartPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> normalFolderDirectoryPaths = [.. (request.NormalFolderDirectoryPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (normalFolderDirectoryPaths.Count == 0 && roots.Count > 0)
        {
            normalFolderDirectoryPaths = [.. Lr2NormalFolderDbSyncService.CreateDirectoryMetadataTargets(roots, chartPaths)];
        }
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
            ? normalFolderDirectoryPaths.Count + folderInfoFilePaths.Count
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
        LogSync(request, "lr2_full_generation_sync input_summary"
            + " roots=" + roots.Count
            + " charts=" + chartPaths.Count
            + " normalFolderDirs=" + normalFolderDirectoryPaths.Count
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
            normalFolderResult = Lr2NormalFolderDbSyncService.Sync(
                songDb,
                CreateNormalFolderDbSyncRequest(
                    request,
                    roots,
                    chartPaths,
                    normalFolderDirectoryPaths,
                    folderInfoFilePaths,
                    directoryEntries));
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
            IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath =
                CreateExistingLr2FolderRowMap(songDb, request, lr2FolderFilePaths);
            Lr2FolderFileSyncItemsResult syncItems = CreateLr2FolderFileSyncItems(
                lr2FolderFilePaths,
                request,
                lr2FolderFileEntries,
                path => existingRowsByPath.TryGetValue(path, out LR2SongDB.folder row) ? row : null);
            Lr2FolderDirectoryMetadataSnapshot lr2FolderParentDirectoryMetadata =
                CreateLr2FolderParentDirectoryMetadataSnapshot(syncItems.Items, request);
            lr2FolderFileResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
            {
                Items = syncItems.Items,
                ScopeDirectories = lr2FolderPruneDirectories,
                DirectoryRowScopeDirectories = CreateLr2FolderDirectoryRowScopeDirectories(request),
                DirectoryRowGenerationScopeDirectories = CreateLr2FolderDirectoryRowGenerationScopeDirectories(request),
                DirectoryMetadataResolver = lr2FolderParentDirectoryMetadata.Resolve,
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

        Lr2FullGenerationSongRowsSkipVerificationResult songRowsSkipVerification =
            TryVerifySongRowsSkip(songDb, request, songRows, resumeCursor, lr2FolderEndCursor, songRowsEndCursor);
        bool skipSongRows = songRowsSkipVerification?.CanSkip == true;
        if (songRowsSkipVerification != null)
        {
            LogSongRowsSkipVerification(request, songRowsSkipVerification, skipSongRows, resumeCursor, lr2FolderEndCursor);
        }

        if (resumeCursor < songRowsEndCursor && !skipSongRows)
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
        if (skipSongRows)
        {
            Lr2FullGenerationStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: songRowsEndCursor,
                totalCount: totalCount,
                stage: "song_rows_completed",
                nowUtc: DateTime.UtcNow);
            ReportProgress(request, songRowsEndCursor, totalCount, "song_rows_completed", songRows.Count, songRows.Count);
        }
        ThrowIfCancellationRequested(songDb, request, skipSongRows ? songRowsEndCursor : Math.Max(folderProcessedCount, resumeCursor), totalCount, "song_rows");

        int songRowStartIndex = skipSongRows
            ? 0
            : Math.Max(0, resumeCursor - lr2FolderEndCursor);
        SongRowSyncResult songRowResult;
        if (resumeCursor >= songRowsEndCursor)
        {
            songRowResult = new SongRowSyncResult(0, 0, 0, 0, 0);
        }
        else if (skipSongRows)
        {
            songRowResult = new SongRowSyncResult(0, songRows.Count, 0, 0, 0);
        }
        else
        {
            songRowResult = UpsertSongRows(
                songDb,
                songRows,
                textFileDirectories,
                songRowStartIndex,
                lr2FolderEndCursor,
                totalCount,
                request.Signature,
                request.RunId,
                request);
        }
        int processedCount = resumeCursor >= songRowsEndCursor || skipSongRows
            ? songRowsEndCursor
            : lr2FolderEndCursor + songRowStartIndex + songRowResult.ProcessedCount;
        ThrowIfCancellationRequested(songDb, request, processedCount, totalCount, "song_rows_completed");

        if (resumeCursor < songRowsEndCursor && !skipSongRows)
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
        else if (skipSongRows)
        {
            LogStage(request, "stage_done", "song_rows", songRows.Count, songRows.Count, processedCount);
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
                LogSync(request, "lr2_full_generation_sync song_row_prune"
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
            bool canResyncNormalFolders = normalFolderResult == null;
            if (diagnosticResult.MissingExpectedFolderRowCount > 0 && roots.Count > 0 && canResyncNormalFolders)
            {
                Lr2NormalFolderDbSyncResult resyncResult = Lr2NormalFolderDbSyncService.Sync(
                    songDb,
                    CreateNormalFolderDbSyncRequest(
                        request,
                        roots,
                        chartPaths,
                        normalFolderDirectoryPaths,
                        folderInfoFilePaths,
                        directoryEntries));
                LogSync(request, "lr2_full_generation_sync startup_scan_blocker_resync"
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
                IReadOnlyDictionary<string, LR2SongDB.folder> existingRowsByPath =
                    CreateExistingLr2FolderRowMap(songDb, request, lr2FolderFilePaths);
                Lr2FolderFileSyncItemsResult syncItems = CreateLr2FolderFileSyncItems(
                    lr2FolderFilePaths,
                    request,
                    lr2FolderFileEntries,
                    path => existingRowsByPath.TryGetValue(path, out LR2SongDB.folder row) ? row : null);
                Lr2FolderDirectoryMetadataSnapshot lr2FolderParentDirectoryMetadata =
                    CreateLr2FolderParentDirectoryMetadataSnapshot(syncItems.Items, request);
                Lr2FolderFileDbSyncResult resyncResult = Lr2FolderFileDbSyncService.Sync(songDb, new Lr2FolderFileDbSyncRequest
                {
                    Items = syncItems.Items,
                    ScopeDirectories = lr2FolderPruneDirectories,
                    DirectoryRowScopeDirectories = CreateLr2FolderDirectoryRowScopeDirectories(request),
                    DirectoryRowGenerationScopeDirectories = CreateLr2FolderDirectoryRowGenerationScopeDirectories(request),
                    DirectoryMetadataResolver = lr2FolderParentDirectoryMetadata.Resolve,
                    AllowPrune = lr2FolderPruneDirectories.Count > 0
                        && request.Lr2FolderFileDiscoveryComplete
                        && !syncItems.HasReadFailures,
                    GeneratedAtUtc = request.StartedAtUtc
                });
                LogSync(request, "lr2_full_generation_sync startup_scan_blocker_resync"
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
                LogSync(request, "lr2_full_generation_sync startup_scan_blocker_cleanup"
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
                LogSync(request, "lr2_full_generation_sync startup_scan_diagnostics_remaining " + diagnosticResult.ToLogDetail()
                    + " total=" + diagnosticResult.TotalBlockerCount
                    + " cleanupFolderRows=" + diagnosticResult.CleanupFolderRowCount
                    + " processedCursor=" + processedCount);
                foreach (string detail in diagnosticResult.EnumerateSampleLogDetails())
                {
                    LogSync(request, "lr2_full_generation_sync startup_scan_diagnostics_detail"
                        + " detail=" + QuoteLogValue(detail));
                }
            }
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
        return new Lr2FullGenerationSyncResult
        {
            TotalCount = totalCount,
            ProcessedCount = processedCount,
            FinalStage = finalStage,
            IncompleteReason = incompleteReason,
            NormalFolderSyncResult = normalFolderResult,
            Lr2FolderFileSyncResult = lr2FolderFileResult,
            Lr2FolderFileProcessedCount = lr2FolderFileProcessedCount,
            SongRowProcessedCount = songRowResult.ProcessedCount,
            SongRowSkippedCount = songRowResult.SkippedCount,
            SongRowParseFailureCount = songRowResult.ParseFailureCount,
            SongRowChartInfoAppliedCount = songRowResult.ChartInfoAppliedCount,
            SongRowLr2CompatibilityAppliedCount = songRowResult.Lr2CompatibilityAppliedCount,
            StaleSongRowPrunedCount = songPruneResult.DeletedCount,
            StartupScanDiagnosticResult = diagnosticResult,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static bool IsSourceCurrent(Lr2FullGenerationSyncRequest request)
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

    private static Lr2FullGenerationSongRowsSkipVerificationResult TryVerifySongRowsSkip(
        LR2SongDBExtended songDb,
        Lr2FullGenerationSyncRequest request,
        IReadOnlyList<BMSFile> songRows,
        int resumeCursor,
        int lr2FolderEndCursor,
        int songRowsEndCursor)
    {
        if (request?.SongRowsSkipVerifier == null || resumeCursor >= songRowsEndCursor)
        {
            return null;
        }
        if (resumeCursor > lr2FolderEndCursor)
        {
            return new Lr2FullGenerationSongRowsSkipVerificationResult
            {
                CanSkip = false,
                Reason = "partial_song_rows_resume",
                TargetRows = songRows?.Count ?? 0
            };
        }

        try
        {
            return request.SongRowsSkipVerifier(songDb, songRows ?? [])
                ?? new Lr2FullGenerationSongRowsSkipVerificationResult
                {
                    CanSkip = false,
                    Reason = "verifier_returned_null",
                    TargetRows = songRows?.Count ?? 0
                };
        }
        catch (Exception ex)
        {
            return new Lr2FullGenerationSongRowsSkipVerificationResult
            {
                CanSkip = false,
                Reason = "verifier_failed_" + ex.GetType().Name,
                TargetRows = songRows?.Count ?? 0
            };
        }
    }

    private static void LogSongRowsSkipVerification(
        Lr2FullGenerationSyncRequest request,
        Lr2FullGenerationSongRowsSkipVerificationResult result,
        bool skipped,
        int resumeCursor,
        int lr2FolderEndCursor)
    {
        if (result == null)
        {
            return;
        }

        LogSync(request, "lr2_full_generation_sync song_rows_skip"
            + " action=" + (skipped ? "skip" : "run")
            + " reason=" + (result.Reason ?? "unknown")
            + " resumeCursor=" + resumeCursor
            + " lr2FolderEndCursor=" + lr2FolderEndCursor
            + " targetRows=" + result.TargetRows
            + " verifiedRows=" + result.VerifiedRows
            + " missingRows=" + result.MissingRows
            + " mismatchedRows=" + result.MismatchedRows
            + " duplicatePathRows=" + result.DuplicatePathRows
            + " digestCheckedRows=" + result.DigestCheckedRows
            + " digestMissingRows=" + result.DigestMissingRows
            + " digestMismatchedRows=" + result.DigestMismatchedRows
            + " projectionMs=" + result.ProjectionMs
            + " existingReadMs=" + result.ExistingReadMs
            + " digestReadMs=" + result.DigestReadMs
            + " elapsedMs=" + result.ElapsedMs);
        foreach (string sample in result.DiagnosticSamples ?? [])
        {
            LogSync(request, "lr2_full_generation_sync song_rows_skip_detail"
                + " reason=" + (result.Reason ?? "unknown")
                + " detail=" + QuoteLogValue(sample));
        }
    }

    private static void ThrowIfCancellationRequested(
        LR2SongDBExtended songDb,
        Lr2FullGenerationSyncRequest request,
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
        Lr2FullGenerationSyncRequest request,
        int processedCount,
        int totalCount,
        string stage,
        int stageProcessedCount,
        int stageTotalCount)
    {
        try
        {
            request?.ProgressReporter?.Invoke(new Lr2FullGenerationSyncProgress
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
            // Progress observation must not affect the durable sync run.
        }
    }

    private static void LogStage(
        Lr2FullGenerationSyncRequest request,
        string action,
        string stage,
        int totalCount,
        int processedCount,
        int processedCursor)
    {
        LogSync(request, "lr2_full_generation_sync " + action
            + " stage=" + (stage ?? string.Empty)
            + " processed=" + Math.Max(0, processedCount)
            + " total=" + Math.Max(0, totalCount)
            + " processedCursor=" + Math.Max(0, processedCursor));
    }

    private static void LogSync(Lr2FullGenerationSyncRequest request, string message)
    {
        try
        {
            request?.LogInstallPerformance?.Invoke(message);
        }
        catch
        {
            // Diagnostics must not affect durable sync semantics.
        }
    }

    private static string QuoteLogValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }

    private static Lr2NormalFolderDbSyncRequest CreateNormalFolderDbSyncRequest(
        Lr2FullGenerationSyncRequest request,
        IReadOnlyCollection<string> roots,
        IReadOnlyCollection<string> chartPaths,
        IReadOnlyCollection<string> normalFolderDirectoryPaths,
        IReadOnlyCollection<string> folderInfoFilePaths,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries)
    {
        return new Lr2NormalFolderDbSyncRequest
        {
            RootDirectories = roots,
            ChartPaths = chartPaths,
            DirectoryPaths = normalFolderDirectoryPaths,
            FolderInfoFilePaths = folderInfoFilePaths,
            FolderInfoFileEntries = request.FolderInfoFileEntries,
            DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(directoryEntries),
            PruneScopeDirectories = roots,
            AllowPrune = true,
            UseScopedExistingRows = true,
            GeneratedAtUtc = request.StartedAtUtc
        };
    }

    private static Lr2StartupScanDiagnosticResult DiagnoseStartupScanBlockers(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> rootDirectories,
        IReadOnlyCollection<string> lr2FolderDiscoveryDirectories,
        IReadOnlyCollection<BMSFile> currentSongRows,
        string lr2RootPath,
        Lr2FullGenerationSyncRequest request = null)
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
        var dateMissingSongRowSamples = new List<string>(10);
        foreach (string currentPath in currentPaths)
        {
            if (!rowsByPath.TryGetValue(currentPath, out StartupDiagnosticSongRow row))
            {
                missingCurrentSongRowCount++;
                continue;
            }
            if (row.Date.HasValue && row.Date.GetValueOrDefault() == 0)
            {
                dateMissingSongRowCount++;
                if (dateMissingSongRowSamples.Count < 10)
                {
                    dateMissingSongRowSamples.Add(currentPath);
                }
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
        HashSet<string> expectedNormalFolderPaths = CreateExpectedNormalFolderRowPaths(
            roots,
            request?.NormalFolderDirectoryPaths,
            currentPaths);
        expectedNormalFolderPaths.UnionWith(CreateExpectedLr2FolderParentDirectoryRowPaths(request));
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
        HashSet<string> expectedPhysicalNormalFolderPaths = RemoveKnownRelativeLr2FolderDirectories(expectedNormalFolderPaths);
        IReadOnlyList<string> missingExpectedFolderRowSamples = GetMissingExpectedRows(
            expectedPhysicalNormalFolderPaths,
            existingNormalFolderPaths,
            10);
        IReadOnlyList<string> missingExpectedLr2FolderRowSamples = GetMissingExpectedRows(
            expectedLr2FolderPaths,
            existingLr2FolderPaths,
            10);
        int missingExpectedFolderRowCount = CountMissingExpectedRows(expectedPhysicalNormalFolderPaths, existingNormalFolderPaths);
        int missingExpectedLr2FolderRowCount = CountMissingExpectedRows(expectedLr2FolderPaths, existingLr2FolderPaths);

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
            dateMissingSongRowSamples,
            missingExpectedFolderRowSamples,
            missingExpectedLr2FolderRowSamples,
            [.. cleanupFolderRowPaths],
            [.. folderDateUpdates.Values]);
    }

    private static HashSet<string> CreateExpectedNormalFolderRowPaths(
        IReadOnlyCollection<string> rootDirectories,
        IReadOnlyCollection<string> normalFolderDirectoryPaths,
        IEnumerable<string> currentChartPaths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootDirectories == null || rootDirectories.Count == 0)
        {
            return result;
        }

        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = rootDirectories,
            DirectoryPaths = normalFolderDirectoryPaths ?? [],
            ChartPaths = currentChartPaths?.ToArray() ?? [],
            DirectoryMetadataResolver = _ => DiagnosticExpectedFolderMetadata
        });

        foreach (LR2SongDB.folder row in generation.Rows ?? [])
        {
            string expectedPath = Lr2FolderPath.ToFolderPath(row?.path);
            if (!string.IsNullOrWhiteSpace(expectedPath))
            {
                result.Add(expectedPath);
            }
        }
        return result;
    }

    internal static Lr2FolderDirectoryMetadataSnapshot CreateLr2FolderParentDirectoryMetadataSnapshot(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        Lr2FullGenerationSyncRequest request)
    {
        IReadOnlyCollection<string> directoryTargets = Lr2FolderFileDbSyncService.CreateParentDirectoryMetadataTargets(
            items,
            CreateLr2FolderDirectoryRowGenerationScopeDirectories(request));
        Lr2FolderInfoCandidateSnapshot surfaceCandidates = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
            request?.FolderInfoFilePaths,
            request?.FolderInfoFileEntries?.Values,
            directoryTargets);
        Dictionary<string, RootFileEnumerationEntry> folderInfoEntriesByPath = MergeEnumerationEntries(
            surfaceCandidates.EntriesByPath);
        Dictionary<string, RootFileEnumerationEntry> directoryEntriesByPath = CreateDirectoryMetadataEntries(
            request?.DirectoryEntries,
            directoryTargets);

        return Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = directoryTargets,
            FolderInfoFilePaths = surfaceCandidates.Paths,
            FolderInfoFileEntries = folderInfoEntriesByPath,
            DirectoryLastWriteTimeUtcResolver = CreateLastWriteTimeResolver(directoryEntriesByPath)
        });
    }

    private static Dictionary<string, RootFileEnumerationEntry> CreateDirectoryMetadataEntries(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> requestEntries,
        IEnumerable<string> directoryTargets)
    {
        HashSet<string> targetSet = CreateDirectoryTargetSet(directoryTargets);
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryEntries(result, requestEntries, targetSet);
        return result;
    }

    private static HashSet<string> CreateDirectoryTargetSet(IEnumerable<string> directoryTargets)
    {
        return new HashSet<string>((directoryTargets ?? [])
            .Select(NormalizeDirectoryPathOrNull)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> sourceEntries,
        ISet<string> targetSet)
    {
        if (result == null || sourceEntries == null || targetSet == null || targetSet.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in NormalizeEnumerationEntries(sourceEntries))
        {
            RootFileEnumerationEntry sourceEntry = pair.Value;
            string path = !string.IsNullOrWhiteSpace(sourceEntry?.Path) ? sourceEntry.Path : pair.Key;
            string key = NormalizeDirectoryPathOrNull(path);
            if (string.IsNullOrWhiteSpace(key) || !targetSet.Contains(key))
            {
                continue;
            }

            if (!result.TryGetValue(key, out RootFileEnumerationEntry existing)
                || existing.LastWriteTimeUtc == null && sourceEntry?.LastWriteTimeUtc != null)
            {
                result[key] = new RootFileEnumerationEntry(key, sourceEntry?.LastWriteTimeUtc, sourceEntry?.FileSize);
            }
        }
    }

    private static readonly Lr2FolderDirectoryMetadata DiagnosticExpectedFolderMetadata =
        new(new DateTime(2026, 1, 1, 0, 0, 1, DateTimeKind.Utc));

    private static HashSet<string> CreateExpectedLr2FolderParentDirectoryRowPaths(Lr2FullGenerationSyncRequest request)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<string> lr2FolderFilePaths = CreateLr2FolderFilePathSurface(request);
        if (lr2FolderFilePaths.Count == 0)
        {
            return result;
        }

        List<string> generationScopes = [.. CreateLr2FolderDirectoryRowGenerationScopeDirectories(request)
            .Select(NormalizeDirectoryPathOrNull)
            .Where(path => !string.IsNullOrWhiteSpace(path))];
        if (generationScopes.Count == 0)
        {
            return result;
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath = NormalizeEnumerationEntries(request.Lr2FolderFileEntries);
        foreach (string filePath in lr2FolderFilePaths)
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
            string directory = NormalizeParentDirectoryPath(databasePath);
            while (!string.IsNullOrWhiteSpace(directory)
                && IsUnderAnyRoot(directory, generationScopes)
                && !generationScopes.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                string folderPath = Lr2FolderPath.ToFolderPath(directory);
                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    result.Add(folderPath);
                }
                directory = NormalizeParentDirectoryPath(directory);
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

    private static int CountMissingExpectedRows(
        IEnumerable<string> expectedPaths,
        ISet<string> existingPaths)
    {
        int missing = 0;
        foreach (string expectedPath in expectedPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(expectedPath)
                && existingPaths?.Contains(expectedPath) != true)
            {
                missing++;
            }
        }
        return missing;
    }

    private static IReadOnlyList<string> GetMissingExpectedRows(
        IEnumerable<string> expectedPaths,
        ISet<string> existingPaths,
        int maxCount)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var result = new List<string>(maxCount);
        foreach (string expectedPath in expectedPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(expectedPath)
                && existingPaths?.Contains(expectedPath) != true)
            {
                result.Add(expectedPath);
                if (result.Count >= maxCount)
                {
                    break;
                }
            }
        }
        return result;
    }

    private static HashSet<string> RemoveKnownRelativeLr2FolderDirectories(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths ?? [])
        {
            if (!IsKnownRelativeLr2FolderDirectory(path))
            {
                result.Add(path);
            }
        }
        return result;
    }

    private static bool IsKnownRelativeLr2FolderDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }
        string normalized = path.Trim()
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(normalized, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(@"LR2files\CustomFolder\", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> CreateExpectedLr2FolderRowPaths(Lr2FullGenerationSyncRequest request)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyCollection<string> lr2FolderFilePaths = CreateLr2FolderFilePathSurface(request);
        if (lr2FolderFilePaths.Count == 0 || request?.Lr2FolderDiscoveryDirectories?.Count > 0 != true)
        {
            return result;
        }

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath = NormalizeEnumerationEntries(request.Lr2FolderFileEntries);
        foreach (string filePath in lr2FolderFilePaths)
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

    private static IReadOnlyCollection<string> CreateLr2FolderFilePathSurface(Lr2FullGenerationSyncRequest request)
    {
        if (request == null)
        {
            return [];
        }

        return [.. (request.Lr2FolderFilePaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Concat(NormalizeEnumerationEntries(request.Lr2FolderFileEntries).Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
        Lr2FullGenerationSyncRequest request,
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
        Lr2FullGenerationSyncRequest request,
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

    private static string NormalizeParentDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            string normalized = path.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
            string directory = Path.GetDirectoryName(normalized);
            return string.IsNullOrWhiteSpace(directory)
                ? null
                : Lr2FolderPath.NormalizeDirectoryPath(directory);
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

    internal static Lr2FolderFileSyncItemsResult CreateLr2FolderFileSyncItems(
        IEnumerable<string> filePaths,
        Lr2FullGenerationSyncRequest request,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath = null,
        Func<string, LR2SongDB.folder> existingRowResolver = null)
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
            Lr2FolderFileSyncItem item = CreateLr2FolderFileSyncItem(filePath, request, entry, existingRowResolver);
            if (item.LastWriteTimeUtc == null)
            {
                hasReadFailures = true;
            }
            items.Add(item);
        }
        return new Lr2FolderFileSyncItemsResult(items, hasReadFailures);
    }

    internal static IReadOnlyDictionary<string, LR2SongDB.folder> CreateExistingLr2FolderRowMap(
        LR2SongDBExtended songDb,
        Lr2FullGenerationSyncRequest request,
        IEnumerable<string> filePaths = null)
    {
        if (songDb == null)
        {
            return new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        }
        string[] exactPaths = [.. CreateExistingLr2FolderExactPathScope(request, filePaths)];
        if (exactPaths.Length == 0)
        {
            return new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        }

        // Keep the pre-parse preservation map ordinal. A casing drift row must be parsed
        // and rewritten so the later sync plan can delete the old key and insert the canonical one.
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        foreach (LR2SongDB.folder row in Lr2FolderExistingRowLookup.QueryExactPaths(songDb, exactPaths))
        {
            if (!string.IsNullOrWhiteSpace(row?.path)
                && string.Equals(Path.GetExtension(row.path), ".lr2folder", StringComparison.OrdinalIgnoreCase)
                && !rowsByPath.ContainsKey(row.path))
            {
                rowsByPath[row.path] = row;
            }
        }
        return rowsByPath;
    }

    private static IReadOnlyCollection<string> CreateExistingLr2FolderExactPathScope(
        Lr2FullGenerationSyncRequest request,
        IEnumerable<string> filePaths)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> sourcePaths = filePaths ?? request?.Lr2FolderFilePaths ?? [];
        foreach (string filePath in sourcePaths)
        {
            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
            {
                FilePath = filePath,
                Lr2RootPath = request?.Lr2RootPath,
                RootCustomFolderOutputBaseDir = request?.Lr2RootCustomFolderOutputBaseDir,
                BuiltinSourceDirectories = request?.Lr2BuiltinFolderSourceDirectories
            });
            AddExistingLr2FolderExactPath(result, classification.DatabasePath);
            AddExistingLr2FolderExactPath(result, filePath);
        }
        return result;
    }

    private static void AddExistingLr2FolderExactPath(ISet<string> result, string path)
    {
        if (result == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            string normalized = Lr2FolderFileProjection.NormalizeDatabasePath(path);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
        }
    }

    internal static IReadOnlyCollection<string> CreateLr2FolderDirectoryRowScopeDirectories(Lr2FullGenerationSyncRequest request)
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
        if (request?.Lr2BuiltinFolderSourceDirectories?.Count > 0
            || (request?.Lr2FolderPruneDirectories ?? []).Any(directory =>
                string.Equals(directory, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(@"LR2files\CustomFolder");
        }
        return [.. candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyCollection<string> CreateLr2FolderDirectoryRowGenerationScopeDirectories(Lr2FullGenerationSyncRequest request)
    {
        var candidates = new List<string>();
        candidates.AddRange(request?.RootDirectories ?? []);

        string normalOutputBase = NormalizeDirectoryPathOrNull(request?.Lr2NormalCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(normalOutputBase))
        {
            candidates.Add(CreateDirectoryRowGenerationBoundary(normalOutputBase));
        }

        string rootOutputBase = NormalizeDirectoryPathOrNull(request?.Lr2RootCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(rootOutputBase))
        {
            candidates.Add(rootOutputBase);
        }

        if (request?.Lr2BuiltinFolderSourceDirectories?.Count > 0
            || (request?.Lr2FolderPruneDirectories ?? []).Any(directory =>
                string.Equals(directory, @"LR2files\CustomFolder", StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(@"LR2files\CustomFolder");
        }

        return [.. candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string CreateDirectoryRowGenerationBoundary(string rootEquivalentDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootEquivalentDirectory))
        {
            return null;
        }
        try
        {
            string normalizedRoot = NormalizeDirectoryPathOrNull(rootEquivalentDirectory);
            string parentDirectory = Path.GetDirectoryName(normalizedRoot?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty);
            return string.IsNullOrWhiteSpace(parentDirectory)
                ? normalizedRoot
                : NormalizeDirectoryPathOrNull(parentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return rootEquivalentDirectory;
        }
    }

    private static Lr2FolderFileSyncItem CreateLr2FolderFileSyncItem(
        string filePath,
        Lr2FullGenerationSyncRequest request,
        RootFileEnumerationEntry enumerationEntry = null,
        Func<string, LR2SongDB.folder> existingRowResolver = null)
    {
        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = filePath,
            Lr2RootPath = request?.Lr2RootPath,
            RootCustomFolderOutputBaseDir = request?.Lr2RootCustomFolderOutputBaseDir,
            BuiltinSourceDirectories = request?.Lr2BuiltinFolderSourceDirectories
        });
        DateTime? lastWriteTimeUtc = ResolveLastWriteTimeUtc(filePath, enumerationEntry);
        string databasePath = classification.DatabasePath;
        if (lastWriteTimeUtc.HasValue
            && existingRowResolver != null
            && CanPreserveExistingLr2FolderRow(databasePath, classification, lastWriteTimeUtc.Value, existingRowResolver(databasePath)))
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                DatabasePath = databasePath,
                LastWriteTimeUtc = lastWriteTimeUtc,
                Definition = null,
                FolderType = classification.FolderType,
                ParentHash = classification.ParentHash,
                PreserveExistingRowOnly = true
            };
        }
        try
        {
            return new Lr2FolderFileSyncItem
            {
                FilePath = filePath,
                DatabasePath = databasePath,
                LastWriteTimeUtc = lastWriteTimeUtc,
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
                DatabasePath = databasePath,
                LastWriteTimeUtc = null,
                Definition = null,
                FolderType = classification.FolderType,
                ParentHash = classification.ParentHash
            };
        }
    }

    private static bool CanPreserveExistingLr2FolderRow(
        string databasePath,
        Lr2FolderFileSourceClassification classification,
        DateTime lastWriteTimeUtc,
        LR2SongDB.folder existingRow)
    {
        if (existingRow == null || string.IsNullOrWhiteSpace(databasePath))
        {
            return false;
        }
        if (existingRow.date != Lr2SongRowEnricher.ToLr2UnixSeconds(lastWriteTimeUtc))
        {
            return false;
        }
        if (existingRow.type != classification.FolderType)
        {
            return false;
        }
        return Lr2FolderFileProjection.TryResolveParentHash(databasePath, classification.ParentHash, out string expectedParentHash)
            && string.Equals(existingRow.parent, expectedParentHash, StringComparison.OrdinalIgnoreCase);
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

    private static Dictionary<string, RootFileEnumerationEntry> MergeEnumerationEntries(
        params IReadOnlyDictionary<string, RootFileEnumerationEntry>[] entrySets)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (IReadOnlyDictionary<string, RootFileEnumerationEntry> entries in entrySets ?? [])
        {
            foreach (KeyValuePair<string, RootFileEnumerationEntry> pair in NormalizeEnumerationEntries(entries))
            {
                if (!result.TryGetValue(pair.Key, out RootFileEnumerationEntry existing)
                    || existing.LastWriteTimeUtc == null && pair.Value?.LastWriteTimeUtc != null)
                {
                    result[pair.Key] = pair.Value;
                }
            }
        }
        return result;
    }

    internal sealed class Lr2FolderFileSyncItemsResult(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        bool hasReadFailures)
    {
        public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; } = items ?? [];

        public bool HasReadFailures { get; } = hasReadFailures;
    }

    private static SongRowSyncResult UpsertSongRows(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<BMSFile> songRows,
        ISet<string> textFileDirectories,
        int startIndex,
        int baseProcessedCursor,
        int totalCount,
        string signature,
        string runId,
        Lr2FullGenerationSyncRequest request)
    {
        if (songRows == null || songRows.Count == 0)
        {
            return new SongRowSyncResult(0, 0, 0, 0, 0);
        }

        List<BMSFile> targetRows = [.. songRows.Where(song => song != null && !string.IsNullOrWhiteSpace(song.path))];
        int safeStartIndex = Math.Max(0, startIndex);
        if (targetRows.Count == 0 || safeStartIndex >= targetRows.Count)
        {
            return new SongRowSyncResult(0, 0, 0, 0, 0);
        }

        BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
        BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        const int songRowSyncChunkSize = 1000;
        int processorCount = Environment.ProcessorCount;
        int workerDegree = ResolveSongRowSyncWorkerDegree(processorCount);
        workerDegree = Math.Max(1, Math.Min(workerDegree, targetRows.Count - safeStartIndex));
        int remainingTargetCount = targetRows.Count - safeStartIndex;
        int readerDegree = ResolveSongRowSyncReaderDegree(processorCount, remainingTargetCount);
        int orderingWindowCapacity = ResolveSongRowSyncOrderingWindowCapacity(
            songRowSyncChunkSize,
            workerDegree,
            readerDegree,
            remainingTargetCount);
        int readQueueCapacity = ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(workerDegree, readerDegree);
        int computedQueueCapacity = Math.Max(songRowSyncChunkSize * 2, workerDegree * 32);
        int processed = 0;
        int transientSkipped = 0;
        int evaluatedStageProcessedCount = safeStartIndex;
        int committedProcessedCursor = baseProcessedCursor + safeStartIndex;
        int parseFailureCount = 0;
        int chartInfoAppliedCount = 0;
        int chartInfoGeneratedCount = 0;
        int chartInfoParseFailurePersistedCount = 0;
        int chartInfoParseFailureClearedCount = 0;
        int chartInfoParseFailureSkippedCount = 0;
        int chartInfoRunCacheHitCount = 0;
        int compatibilityApplied = 0;
        long totalReadTicks = 0L;
        long totalDigestTicks = 0L;
        long totalParseTicks = 0L;
        var generatedChartInfoBySha256 = new ConcurrentDictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        var generatedChartInfoByMd5 = new ConcurrentDictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        Func<BMSFile, LR2SongDBExtended.chart_info> baseChartInfoResolver =
            CreateSongRowSyncChartInfoResolver(
                request?.ChartInfoResolver,
                request?.ChartInfoResolverIsThreadSafe == true);
        Func<BMSFile, LR2SongDBExtended.chart_info> chartInfoResolver = row =>
        {
            if (row == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256)
                && generatedChartInfoBySha256.TryGetValue(row.sha256, out LR2SongDBExtended.chart_info bySha256))
            {
                Interlocked.Increment(ref chartInfoRunCacheHitCount);
                return bySha256;
            }
            if (!string.IsNullOrWhiteSpace(row.hash)
                && generatedChartInfoByMd5.TryGetValue(row.hash, out LR2SongDBExtended.chart_info byMd5))
            {
                Interlocked.Increment(ref chartInfoRunCacheHitCount);
                return byMd5;
            }
            return baseChartInfoResolver?.Invoke(row);
        };
        void CacheGeneratedChartInfo(LR2SongDBExtended.chart_info row)
        {
            if (row == null || row.parser_version < BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256))
            {
                generatedChartInfoBySha256.TryAdd(row.sha256, row);
            }
            if (!string.IsNullOrWhiteSpace(row.md5))
            {
                generatedChartInfoByMd5.TryAdd(row.md5, row);
            }
        }
        using var readQueue = new BlockingCollection<SongRowSyncReadCandidate>(readQueueCapacity);
        using var computedQueue = new BlockingCollection<SongRowSyncComputedItem>(computedQueueCapacity);
        long readerOutputWaitTicks = 0L;
        long workerOutputWaitTicks = 0L;
        int readQueueHighWatermark = 0;
        int computedQueueHighWatermark = 0;
        int pendingItemsHighWatermark = 0;
        int writerFailed = 0;
        Exception pipelineException = null;
        CancellationToken cancellationToken = request?.CancellationToken ?? CancellationToken.None;
        using var pipelineCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken pipelineToken = pipelineCancellationSource.Token;
        using var orderingWindow = new SemaphoreSlim(orderingWindowCapacity, orderingWindowCapacity);
        ISet<string> transientSkipPaths = safeStartIndex == 0
            ? request?.TransientSongRowsSkipPaths
            : null;
        int transientSkipPathCount = transientSkipPaths?.Count ?? 0;
        LogSync(request, "lr2_full_generation_sync pipeline_start"
            + " stage=song_rows"
            + " startIndex=" + safeStartIndex
            + " targetCount=" + targetRows.Count
            + " transientSkipPaths=" + transientSkipPathCount
            + " chunkSize=" + songRowSyncChunkSize
            + " readerDegree=" + readerDegree
            + " workerDegree=" + workerDegree
            + " processorCount=" + processorCount
            + " maxWorkerDegree=" + SongRowSyncMaxWorkerDegree
            + " orderingWindowCapacity=" + orderingWindowCapacity
            + " readQueueCapacity=" + readQueueCapacity
            + " computedQueueCapacity=" + computedQueueCapacity);
        ThrowIfCancellationRequested(
            songDb,
            request,
            baseProcessedCursor + safeStartIndex,
            totalCount,
            "song_rows");
        long lastWorkerProgressTicks = Stopwatch.GetTimestamp();

        void ReportSongRowsWorkerProgress(int stageProcessedCount, bool force = false)
        {
            if (!force)
            {
                long now = Stopwatch.GetTimestamp();
                if (TicksToMilliseconds(now - lastWorkerProgressTicks) < 150)
                {
                    return;
                }
                lastWorkerProgressTicks = now;
            }
            ReportProgress(
                request,
                Volatile.Read(ref committedProcessedCursor),
                totalCount,
                "song_rows",
                stageProcessedCount,
                targetRows.Count);
        }

        void FlushSongRowChunk(List<SongRowSyncComputedItem> chunk)
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
            LogSync(request, "lr2_full_generation_sync chunk_start"
                + " stage=song_rows"
                + " offset=" + offset
                + " count=" + chunk.Count
                + " processedCursor=" + (baseProcessedCursor + offset));
            var rowsToWrite = new List<BMSFile>(chunk.Count);
            long chunkReadTicks = 0L;
            long chunkDigestTicks = 0L;
            long chunkParseTicks = 0L;
            int chunkFallbackCount = 0;
            int chunkParseFailureCount = 0;
            int chunkTransientSkippedCount = 0;
            foreach (SongRowSyncComputedItem item in chunk)
            {
                chunkReadTicks += item.ReadElapsedTicks;
                chunkDigestTicks += item.DigestElapsedTicks;
                chunkParseTicks += item.ParseElapsedTicks;
                if (item.TransientSkipped)
                {
                    chunkTransientSkippedCount++;
                    continue;
                }
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
            totalReadTicks += chunkReadTicks;
            totalDigestTicks += chunkDigestTicks;
            totalParseTicks += chunkParseTicks;

            long chunkChartInfoTicks = 0L;
            long chunkCompatibilityTicks = 0L;
            int chunkChartInfoAppliedCount = 0;
            var chunkCompatibilityInfos = new List<BMSFileMaintenanceInfo>();
            var chunkChartInfoRows = new List<LR2SongDBExtended.chart_info>();
            var chunkChartInfoParseFailures = new List<LR2SongDBExtended.chart_info_parse_failure>();
            var chunkChartInfoParseFailureDeleteMd5s = new List<string>();
            foreach (SongRowSyncComputedItem item in chunk)
            {
                chunkChartInfoTicks += item.ChartInfoElapsedTicks;
                chunkCompatibilityTicks += item.CompatibilityElapsedTicks;
                if (item.ChartInfoApplied)
                {
                    chunkChartInfoAppliedCount++;
                }
                if (item.Lr2CompatibilityInfo != null)
                {
                    chunkCompatibilityInfos.Add(item.Lr2CompatibilityInfo);
                }
                if (item.GeneratedChartInfoRow != null)
                {
                    chunkChartInfoRows.Add(item.GeneratedChartInfoRow);
                }
                if (item.ChartInfoParseFailureRow != null)
                {
                    chunkChartInfoParseFailures.Add(item.ChartInfoParseFailureRow);
                }
                if (!string.IsNullOrWhiteSpace(item.ChartInfoParseFailureDeleteMd5))
                {
                    chunkChartInfoParseFailureDeleteMd5s.Add(item.ChartInfoParseFailureDeleteMd5);
                }
            }
            var stopwatchCommit = Stopwatch.StartNew();
            long songStageMs = 0L;
            long chartInfoStageMs = 0L;
            long compatibilityStageMs = 0L;
            long sqliteCommitMs = 0L;
            long statusCursorMs = 0L;
            Lr2GeneratedSongWriteResult songWriteResult = default;
            Lr2CompatibilityFactsWriteResult compatibilityWriteResult = Lr2CompatibilityFactsWriteResult.Empty;
            songDb.BeginTransaction();
            try
            {
                var stageStopwatch = Stopwatch.StartNew();
                songWriteResult = Lr2SongDbWriter.UpsertGeneratedSongsForFullGenerationWithResult(songDb, rowsToWrite);
                stageStopwatch.Stop();
                songStageMs = stageStopwatch.ElapsedMilliseconds;

                stageStopwatch.Restart();
                BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
                    songDb,
                    [],
                    chunkChartInfoRows,
                    chunkChartInfoParseFailures,
                    chunkChartInfoParseFailureDeleteMd5s);
                stageStopwatch.Stop();
                chartInfoStageMs = stageStopwatch.ElapsedMilliseconds;

                stageStopwatch.Restart();
                compatibilityWriteResult = UpsertLr2CompatibilityFacts(songDb, chunkCompatibilityInfos);
                stageStopwatch.Stop();
                compatibilityStageMs = stageStopwatch.ElapsedMilliseconds;

                stageStopwatch.Restart();
                songDb.Commit();
                stageStopwatch.Stop();
                sqliteCommitMs = stageStopwatch.ElapsedMilliseconds;
                stopwatchCommit.Stop();
                ReportCommittedChartInfoRows(request, chunkChartInfoRows);
                ReportCommittedChartInfoParseFailures(
                    request,
                    chunkChartInfoParseFailures.Count,
                    chunkChartInfoParseFailureDeleteMd5s.Count);
                ReportCommittedLr2CompatibilityFacts(request, chunkCompatibilityInfos);
                chartInfoGeneratedCount += chunkChartInfoRows.Count;
                chartInfoParseFailurePersistedCount += chunkChartInfoParseFailures.Count;
                chartInfoParseFailureClearedCount += chunkChartInfoParseFailureDeleteMd5s.Count;
                chartInfoParseFailureSkippedCount += chunk.Count(item => item.ChartInfoParseFailureSkipped);
                compatibilityApplied += chunkCompatibilityInfos.Count;
                parseFailureCount += chunkParseFailureCount;
                chartInfoAppliedCount += chunkChartInfoAppliedCount;
                transientSkipped += chunkTransientSkippedCount;
                processed += chunk.Count;
                int processedCursor = baseProcessedCursor + offset + chunk.Count;
                stageStopwatch.Restart();
                Lr2FullGenerationStatusService.UpdateCursor(
                    songDb,
                    signature,
                    runId,
                    processedCursor,
                    totalCount,
                    stage: "song_rows",
                    nowUtc: DateTime.UtcNow);
                stageStopwatch.Stop();
                statusCursorMs = stageStopwatch.ElapsedMilliseconds;
                Volatile.Write(ref committedProcessedCursor, processedCursor);
                ReportProgress(
                    request,
                    processedCursor,
                    totalCount,
                    "song_rows",
                    Math.Max(Volatile.Read(ref evaluatedStageProcessedCount), offset + chunk.Count),
                    targetRows.Count);
                LogSync(request, "lr2_full_generation_sync chunk_done"
                    + " stage=song_rows"
                    + " offset=" + offset
                    + " count=" + chunk.Count
                    + " readMs=" + TicksToMilliseconds(chunkReadTicks)
                    + " digestMs=" + TicksToMilliseconds(chunkDigestTicks)
                    + " parseMs=" + TicksToMilliseconds(chunkParseTicks)
                    + " chartInfoApplyMs=" + TicksToMilliseconds(chunkChartInfoTicks)
                    + " chartInfoGenerated=" + chunkChartInfoRows.Count
                    + " chartInfoParseFailurePersisted=" + chunkChartInfoParseFailures.Count
                    + " chartInfoParseFailureCleared=" + chunkChartInfoParseFailureDeleteMd5s.Count
                    + " chartInfoParseFailureSkipped=" + chunk.Count(item => item.ChartInfoParseFailureSkipped)
                    + " compatibilityBuildMs=" + TicksToMilliseconds(chunkCompatibilityTicks)
                    + " commitMs=" + stopwatchCommit.ElapsedMilliseconds
                    + " songStageMs=" + songStageMs
                    + " songChanged=" + songWriteResult.ChangedCount
                    + " songUpdated=" + songWriteResult.UpdatedCount
                    + " songInserted=" + songWriteResult.InsertedCount
                    + " songEnrichMs=" + songWriteResult.EnrichmentMs
                    + " songTempMs=" + songWriteResult.TempStageMs
                    + " songPreviousHashMs=" + songWriteResult.PreviousHashStageMs
                    + " songUpdateMs=" + songWriteResult.UpdateStageMs
                    + " songInsertMs=" + songWriteResult.InsertStageMs
                    + " songDigestUpsertMs=" + songWriteResult.DigestUpsertStageMs
                    + " songDigestCleanupMs=" + songWriteResult.DigestCleanupStageMs
                    + " songTempCleanupMs=" + songWriteResult.TempCleanupStageMs
                    + " chartInfoStageMs=" + chartInfoStageMs
                    + " compatibilityStageMs=" + compatibilityStageMs
                    + " compatibilityUpdated=" + compatibilityWriteResult.UpdatedCount
                    + " compatibilityInserted=" + compatibilityWriteResult.InsertedCount
                    + " sqliteCommitMs=" + sqliteCommitMs
                    + " statusCursorMs=" + statusCursorMs
                    + " fallbackCount=" + chunkFallbackCount
                    + " parseFailureCount=" + chunkParseFailureCount
                    + " transientSkipped=" + chunkTransientSkippedCount
                    + " compatibilityApplied=" + chunkCompatibilityInfos.Count
                    + " processedCursor=" + processedCursor
                    + " managedBytes=" + GC.GetTotalMemory(false));
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

        int nextReadIndex = safeStartIndex - 1;
        Task[] readerTasks = [.. Enumerable.Range(0, readerDegree)
            .Select(_ => Task.Run(delegate
            {
                while (true)
                {
                    if (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }

                    int index = Interlocked.Increment(ref nextReadIndex);
                    if (index >= targetRows.Count)
                    {
                        break;
                    }

                    bool windowSlotAcquired = false;
                    bool windowSlotTransferred = false;
                    try
                    {
                        orderingWindow.Wait(pipelineToken);
                        windowSlotAcquired = true;
                        if (Volatile.Read(ref writerFailed) != 0)
                        {
                            break;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        SongRowSyncReadCandidate candidate = ShouldTransientSkipSongRow(targetRows[index], transientSkipPaths)
                            ? SongRowSyncReadCandidate.CreateTransientSkipped(index, targetRows[index])
                            : ReadSyncSongRowCandidate(index, targetRows[index]);
                        AddWithWait(readQueue, candidate, ref readerOutputWaitTicks, pipelineToken);
                        windowSlotTransferred = true;
                        UpdateHighWatermark(ref readQueueHighWatermark, readQueue.Count);
                    }
                    catch (InvalidOperationException) when (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                    finally
                    {
                        if (windowSlotAcquired && !windowSlotTransferred)
                        {
                            try
                            {
                                orderingWindow.Release();
                            }
                            catch (SemaphoreFullException)
                            {
                            }
                        }
                    }
                }
            }))];
        Task readerCompletionTask = Task.WhenAll(readerTasks).ContinueWith(_ => TryCompleteAdding(readQueue));

        Task[] workerTasks = [.. Enumerable.Range(0, workerDegree)
            .Select(_ => Task.Run(delegate
            {
                foreach (SongRowSyncReadCandidate candidate in readQueue.GetConsumingEnumerable(pipelineToken))
                {
                    if (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    SongRowSyncComputedItem item = CreateSyncSongRowItem(
                        candidate,
                        textFileDirectories,
                        chartInfoResolver,
                        CacheGeneratedChartInfo,
                        request?.ChartInfoParseTimeout,
                        request?.CurrentChartInfoParseFailureMd5s);
                    int evaluatedStageProcessed = Interlocked.Increment(ref evaluatedStageProcessedCount);
                    ReportSongRowsWorkerProgress(evaluatedStageProcessed);
                    try
                    {
                        AddWithWait(computedQueue, item, ref workerOutputWaitTicks, pipelineToken);
                        UpdateHighWatermark(ref computedQueueHighWatermark, computedQueue.Count);
                    }
                    catch (InvalidOperationException) when (Volatile.Read(ref writerFailed) != 0)
                    {
                        break;
                    }
                }
            }))];
        Task workerCompletionTask = Task.WhenAll(workerTasks).ContinueWith(_ => TryCompleteAdding(computedQueue));
        var pendingItems = new SortedDictionary<int, SongRowSyncComputedItem>();
        var writerChunk = new List<SongRowSyncComputedItem>(songRowSyncChunkSize);
        int nextIndexToCommit = safeStartIndex;
        try
        {
            foreach (SongRowSyncComputedItem item in computedQueue.GetConsumingEnumerable(pipelineToken))
            {
                pendingItems[item.Index] = item;
                UpdateHighWatermark(ref pendingItemsHighWatermark, pendingItems.Count);
                while (pendingItems.TryGetValue(nextIndexToCommit, out SongRowSyncComputedItem nextItem))
                {
                    pendingItems.Remove(nextIndexToCommit);
                    writerChunk.Add(nextItem);
                    orderingWindow.Release();
                    nextIndexToCommit++;
                    if (writerChunk.Count >= songRowSyncChunkSize)
                    {
                        FlushSongRowChunk(writerChunk);
                        writerChunk = new List<SongRowSyncComputedItem>(songRowSyncChunkSize);
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
            pipelineCancellationSource.Cancel();
            TryCompleteAdding(readQueue);
            TryCompleteAdding(computedQueue);
        }

        try
        {
            Task.WaitAll(readerTasks);
            readerCompletionTask.Wait();
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

        LogSync(request, "lr2_full_generation_sync pipeline_done"
            + " stage=song_rows"
            + " processed=" + processed
            + " transientSkipped=" + transientSkipped
            + " parseFailureCount=" + parseFailureCount
            + " chartInfoApplied=" + chartInfoAppliedCount
            + " chartInfoGenerated=" + chartInfoGeneratedCount
            + " chartInfoParseFailurePersisted=" + chartInfoParseFailurePersistedCount
            + " chartInfoParseFailureCleared=" + chartInfoParseFailureClearedCount
            + " chartInfoParseFailureSkipped=" + chartInfoParseFailureSkippedCount
            + " chartInfoRunCacheHits=" + chartInfoRunCacheHitCount
            + " compatibilityApplied=" + compatibilityApplied
            + " readMs=" + TicksToMilliseconds(totalReadTicks)
            + " digestMs=" + TicksToMilliseconds(totalDigestTicks)
            + " parseMs=" + TicksToMilliseconds(totalParseTicks)
            + " readerOutputWaitMs=" + TicksToMilliseconds(readerOutputWaitTicks)
            + " workerOutputWaitMs=" + TicksToMilliseconds(workerOutputWaitTicks)
            + " readQueueHighWatermark=" + readQueueHighWatermark
            + " computedQueueHighWatermark=" + computedQueueHighWatermark
            + " pendingItemsHighWatermark=" + pendingItemsHighWatermark);
        return new SongRowSyncResult(processed, transientSkipped, parseFailureCount, chartInfoAppliedCount, compatibilityApplied);
    }

    private static void ReportCommittedLr2CompatibilityFacts(
        Lr2FullGenerationSyncRequest request,
        IReadOnlyList<BMSFileMaintenanceInfo> compatibilityInfos)
    {
        if (request?.Lr2CompatibilityFactsCommitted == null
            || compatibilityInfos == null
            || compatibilityInfos.Count == 0)
        {
            return;
        }

        try
        {
            request.Lr2CompatibilityFactsCommitted(compatibilityInfos);
        }
        catch (Exception ex)
        {
            LogSync(request, "lr2_full_generation_sync compatibility_projection_callback_failed"
                + " count=" + compatibilityInfos.Count
                + " reason=" + QuoteLogValue(ex.Message ?? ex.GetType().Name));
        }
    }

    private static void ReportCommittedChartInfoRows(
        Lr2FullGenerationSyncRequest request,
        IReadOnlyList<LR2SongDBExtended.chart_info> chartInfoRows)
    {
        if (request?.ChartInfoRowsCommitted == null
            || chartInfoRows == null
            || chartInfoRows.Count == 0)
        {
            return;
        }

        try
        {
            request.ChartInfoRowsCommitted(chartInfoRows);
        }
        catch (Exception ex)
        {
            LogSync(request, "lr2_full_generation_sync chart_info_callback_failed"
                + " count=" + chartInfoRows.Count
                + " reason=" + QuoteLogValue(ex.Message ?? ex.GetType().Name));
        }
    }

    private static void ReportCommittedChartInfoParseFailures(
        Lr2FullGenerationSyncRequest request,
        int persistedCount,
        int clearedCount)
    {
        if (request?.ChartInfoParseFailuresCommitted == null
            || (persistedCount <= 0 && clearedCount <= 0))
        {
            return;
        }

        try
        {
            request.ChartInfoParseFailuresCommitted(persistedCount, clearedCount);
        }
        catch (Exception ex)
        {
            LogSync(request, "lr2_full_generation_sync chart_info_failure_callback_failed"
                + " persisted=" + persistedCount
                + " cleared=" + clearedCount
                + " reason=" + QuoteLogValue(ex.Message ?? ex.GetType().Name));
        }
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

    private static int ResolveSongRowSyncWorkerDegree(int processorCount)
    {
        return ChartFileReadPipelinePolicy.ResolveCpuWorkerDegree(processorCount, SongRowSyncMaxWorkerDegree);
    }

    private static int ResolveSongRowSyncReaderDegree(int processorCount, int remainingTargetCount)
    {
        return ChartFileReadPipelinePolicy.ResolveReaderDegree(processorCount, remainingTargetCount);
    }

    private static int ResolveSongRowSyncOrderingWindowCapacity(
        int chunkSize,
        int workerDegree,
        int readerDegree,
        int remainingTargetCount)
    {
        int minimum = Math.Max(chunkSize * 2, 1);
        int workerBufferedChunks = Math.Max(4, (workerDegree / 2) + readerDegree);
        int desired = chunkSize * workerBufferedChunks;
        int maximum = chunkSize * 8;
        return Math.Max(1, Math.Min(remainingTargetCount, Math.Min(maximum, Math.Max(minimum, desired))));
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

    private static SongRowSyncReadCandidate ReadSyncSongRowCandidate(int index, BMSFile existingSong)
    {
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.path))
        {
            return new SongRowSyncReadCandidate(index, existingSong, null, 0L);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChartFileReadBuffer buffer = ChartFileContentReader.ReadBuffer(existingSong.path);
            stopwatch.Stop();
            return new SongRowSyncReadCandidate(index, existingSong, buffer, stopwatch.ElapsedTicks);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return new SongRowSyncReadCandidate(index, existingSong, null, 0L);
        }
    }

    private static bool ShouldTransientSkipSongRow(BMSFile row, ISet<string> transientSkipPaths)
    {
        return row != null
            && !string.IsNullOrWhiteSpace(row.path)
            && transientSkipPaths != null
            && transientSkipPaths.Contains(row.path);
    }

    private static SongRowSyncComputedItem CreateSyncSongRowItem(
        SongRowSyncReadCandidate candidate,
        ISet<string> textFileDirectories,
        Func<BMSFile, LR2SongDBExtended.chart_info> chartInfoResolver,
        Action<LR2SongDBExtended.chart_info> generatedChartInfoAvailable,
        TimeSpan? chartInfoParseTimeout,
        ISet<string> currentChartInfoParseFailureMd5s)
    {
        if (candidate?.TransientSkipped == true)
        {
            return SongRowSyncComputedItem.CreateTransientSkipped(candidate.Index);
        }

        ChartFileSnapshot snapshot = CreateSyncSongRowSnapshot(candidate, out long digestTicks);
        BMSFile row = CreateSyncSongRow(
            candidate,
            snapshot,
            textFileDirectories,
            out bool parsedFromSnapshot,
            out long parseTicks);
        bool chartInfoApplied = false;
        long chartInfoTicks = 0L;
        LR2SongDBExtended.chart_info generatedChartInfoRow = null;
        LR2SongDBExtended.chart_info_parse_failure chartInfoParseFailureRow = null;
        string chartInfoParseFailureDeleteMd5 = null;
        bool chartInfoParseFailureSkipped = false;
        if (row != null)
        {
            long chartInfoStart = Stopwatch.GetTimestamp();
            LR2SongDBExtended.chart_info chartInfo = chartInfoResolver?.Invoke(row);
            if (!IsUsableChartInfo(row, chartInfo))
            {
                chartInfo = null;
            }
            if (chartInfo == null && IsCurrentChartInfoParseFailure(row, candidate, snapshot, currentChartInfoParseFailureMd5s))
            {
                chartInfoParseFailureSkipped = true;
            }
            else if (chartInfo == null && TryBuildChartInfoFromSnapshot(
                candidate,
                snapshot,
                chartInfoParseTimeout,
                out generatedChartInfoRow,
                out chartInfoParseFailureRow,
                out chartInfoParseFailureDeleteMd5))
            {
                chartInfo = generatedChartInfoRow;
                generatedChartInfoAvailable?.Invoke(generatedChartInfoRow);
            }
            chartInfoApplied = TryApplyChartInfoRow(row, chartInfo);
            Lr2SongRowEnricher.ApplyLr2ChartMetadataDefaults(row);
            chartInfoTicks = Stopwatch.GetTimestamp() - chartInfoStart;
        }

        BMSFileMaintenanceInfo compatibilityInfo = null;
        long compatibilityTicks = 0L;
        if (row != null)
        {
            long compatibilityStart = Stopwatch.GetTimestamp();
            TryCreateLr2CompatibilityMaintenanceInfo(row, out compatibilityInfo);
            compatibilityTicks = Stopwatch.GetTimestamp() - compatibilityStart;
            row.ClearResourceReferenceCollections();
        }

        return new SongRowSyncComputedItem(
            candidate.Index,
            row,
            parsedFromSnapshot,
            candidate.ReadElapsedTicks,
            digestTicks,
            parseTicks,
            chartInfoApplied,
            chartInfoTicks,
            generatedChartInfoRow,
            chartInfoParseFailureRow,
            chartInfoParseFailureDeleteMd5,
            chartInfoParseFailureSkipped,
            compatibilityInfo,
            compatibilityTicks);
    }

    private static ChartFileSnapshot CreateSyncSongRowSnapshot(
        SongRowSyncReadCandidate candidate,
        out long digestTicks)
    {
        digestTicks = 0L;
        if (candidate?.Buffer == null)
        {
            return null;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChartFileSnapshot snapshot = ChartFileContentReader.CreateSnapshot(candidate.Buffer);
            stopwatch.Stop();
            digestTicks = stopwatch.ElapsedTicks;
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return null;
        }
    }

    private static bool IsCurrentChartInfoParseFailure(
        BMSFile row,
        SongRowSyncReadCandidate candidate,
        ChartFileSnapshot snapshot,
        ISet<string> currentChartInfoParseFailureMd5s)
    {
        if (currentChartInfoParseFailureMd5s == null || currentChartInfoParseFailureMd5s.Count == 0)
        {
            return false;
        }

        string md5 = !string.IsNullOrWhiteSpace(row?.hash)
            ? row.hash
            : (!string.IsNullOrWhiteSpace(snapshot?.Md5)
                ? snapshot.Md5
                : candidate?.ExistingSong?.hash);
        return !string.IsNullOrWhiteSpace(md5) && currentChartInfoParseFailureMd5s.Contains(md5);
    }

    private static bool TryBuildChartInfoFromSnapshot(
        SongRowSyncReadCandidate candidate,
        ChartFileSnapshot snapshot,
        TimeSpan? parseTimeout,
        out LR2SongDBExtended.chart_info row,
        out LR2SongDBExtended.chart_info_parse_failure parseFailureRow,
        out string parseFailureDeleteMd5)
    {
        row = null;
        parseFailureRow = null;
        parseFailureDeleteMd5 = null;
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Path))
        {
            return false;
        }

        string md5 = !string.IsNullOrWhiteSpace(snapshot.Md5)
            ? snapshot.Md5
            : candidate?.ExistingSong?.hash;
        string sha256 = !string.IsNullOrWhiteSpace(snapshot.Sha256)
            ? snapshot.Sha256
            : candidate?.ExistingSong?.sha256;
        try
        {
            ChartInfoParser.ChartInfoParseResult parseResult = ChartInfoParser.ParseBytesDetailed(
                snapshot.Bytes,
                snapshot.Path,
                md5,
                sha256,
                encodingName: null,
                timeout: parseTimeout);
            row = parseResult.Row;
            if (!string.IsNullOrWhiteSpace(row?.md5))
            {
                parseFailureDeleteMd5 = row.md5;
            }
            return row != null;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(md5))
            {
                bool timeoutFailed = ex is ChartInfoParser.ChartInfoParseTimeoutException;
                parseFailureRow = new LR2SongDBExtended.chart_info_parse_failure
                {
                    md5 = md5,
                    sha256 = sha256,
                    path = snapshot.Path,
                    parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    failure_kind = timeoutFailed ? "timeout" : "parse_failed",
                    exception_type = ex.GetType().Name,
                    message = NormalizePersistedChartInfoParseFailureMessage(ex.Message),
                    parse_timeout_ms = timeoutFailed && parseTimeout.HasValue
                        ? Math.Max(0, (int)Math.Ceiling(parseTimeout.Value.TotalMilliseconds))
                        : null,
                    updated_at = DateTime.UtcNow
                };
            }
            return false;
        }
    }

    private static string NormalizePersistedChartInfoParseFailureMessage(string message)
    {
        string normalized = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        return normalized.Length <= MaxPersistedChartInfoParseFailureMessageLength
            ? normalized
            : normalized.Substring(0, MaxPersistedChartInfoParseFailureMessageLength);
    }

    private static BMSFile CreateSyncSongRow(
        SongRowSyncReadCandidate candidate,
        ChartFileSnapshot snapshot,
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
        if (snapshot == null)
        {
            return CreateFallbackSyncSongRow(existingSong, textFileDirectories);
        }

        try
        {
            var stopwatchParse = Stopwatch.StartNew();
            BMSFile parsed = Lr2SongRowEnricher.CreateParsedSongRowFromSnapshot(
                snapshot,
                ResolveTextGroupFlag(existingSong.path, textFileDirectories, existingSong.txt.GetValueOrDefault()),
                existingSong);
            stopwatchParse.Stop();
            parseTicks = stopwatchParse.ElapsedTicks;
            parsedFromSnapshot = true;
            return parsed;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return CreateFallbackSyncSongRow(existingSong, textFileDirectories);
        }
    }

    private static long TicksToMilliseconds(long ticks)
    {
        return ticks <= 0L ? 0L : (long)(ticks * 1000.0 / Stopwatch.Frequency);
    }

    private static BMSFile CreateFallbackSyncSongRow(BMSFile existingSong, ISet<string> textFileDirectories)
    {
        BMSFile copy = existingSong?.CreateSongRowPersistenceCopy();
        copy?.SetTextGroupFlag(ResolveTextGroupFlag(existingSong?.path, textFileDirectories, existingSong?.txt.GetValueOrDefault() ?? 0));
        return copy;
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

    private static bool TryApplyChartInfoRow(
        BMSFile row,
        LR2SongDBExtended.chart_info chartInfo)
    {
        if (row == null || chartInfo == null)
        {
            return false;
        }
        if (!IsUsableChartInfo(row, chartInfo))
        {
            return false;
        }

        row.ApplyLr2ChartInfoDetailedColumns(chartInfo);
        return true;
    }

    private static Func<BMSFile, LR2SongDBExtended.chart_info> CreateSongRowSyncChartInfoResolver(
        Func<BMSFile, LR2SongDBExtended.chart_info> requestResolver,
        bool requestResolverIsThreadSafe)
    {
        if (requestResolver != null)
        {
            if (requestResolverIsThreadSafe)
            {
                return requestResolver;
            }

            object sync = new();
            return row =>
            {
                lock (sync)
                {
                    return requestResolver(row);
                }
            };
        }
        return null;
    }

    private static Func<BMSFile, LR2SongDBExtended.chart_info> CreateChartInfoResolver(
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> bySha256,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info> byMd5)
    {
        return row =>
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
        };
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

    private static bool IsUsableChartInfo(BMSFile row, LR2SongDBExtended.chart_info chartInfo)
    {
        return chartInfo != null
            && chartInfo.parser_version >= BmsLibraryDbGateway.CurrentChartInfoParserVersion
            && IsChartInfoCompatible(row, chartInfo);
    }

    private sealed class Lr2CompatibilityFactsWriteResult(int updatedCount, int insertedCount)
    {
        internal static Lr2CompatibilityFactsWriteResult Empty { get; } = new(0, 0);

        internal int UpdatedCount { get; } = updatedCount;

        internal int InsertedCount { get; } = insertedCount;
    }

    private static Lr2CompatibilityFactsWriteResult UpsertLr2CompatibilityFacts(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<BMSFileMaintenanceInfo> infos)
    {
        if (songDb == null)
        {
            return Lr2CompatibilityFactsWriteResult.Empty;
        }
        List<BMSFileMaintenanceInfo> rows = [.. (infos ?? [])
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.path))];
        if (rows.Count == 0)
        {
            return Lr2CompatibilityFactsWriteResult.Empty;
        }

        PrepareTempLr2CompatibilityMaintenanceTable(songDb);
        BulkInsertLr2CompatibilityMaintenanceTempRows(songDb, rows);

        string tableName = SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.path);
        string hashColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.hash);
        string pathFlagsColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_path_warning_flags);
        string chartPathBytesColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_chart_path_cp932_bytes);
        string folderScanBytesColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_folder_scan_cp932_bytes);
        string resourceFlagsColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_warning_flags);
        string maxRawBytesColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_max_raw_cp932_bytes);
        string maxResolvedBytesColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_max_resolved_cp932_bytes);
        string unsupportedCountColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_unsupported_count);

        string tempName = "temp." + TempLr2CompatibilityMaintenanceTable;
        string matchTempName = "temp." + TempLr2CompatibilityMaintenanceMatchTable;
        string changedPredicate =
            "m." + hashColumn + " IS NOT t.hash"
            + " OR m." + pathFlagsColumn + " IS NOT t.lr2_path_warning_flags"
            + " OR m." + chartPathBytesColumn + " IS NOT t.lr2_chart_path_cp932_bytes"
            + " OR m." + folderScanBytesColumn + " IS NOT t.lr2_folder_scan_cp932_bytes"
            + " OR m." + resourceFlagsColumn + " IS NOT t.lr2_resource_warning_flags"
            + " OR m." + maxRawBytesColumn + " IS NOT t.lr2_resource_max_raw_cp932_bytes"
            + " OR m." + maxResolvedBytesColumn + " IS NOT t.lr2_resource_max_resolved_cp932_bytes"
            + " OR m." + unsupportedCountColumn + " IS NOT t.lr2_resource_unsupported_count";
        PrepareTempLr2CompatibilityMaintenanceMatchTable(songDb);
        songDb.Execute(
            "INSERT OR REPLACE INTO " + matchTempName
            + " (target_rowid, hash, lr2_path_warning_flags, lr2_chart_path_cp932_bytes, lr2_folder_scan_cp932_bytes, "
            + "lr2_resource_warning_flags, lr2_resource_max_raw_cp932_bytes, lr2_resource_max_resolved_cp932_bytes, lr2_resource_unsupported_count) "
            + "SELECT m.rowid"
            + ", t.hash"
            + ", t.lr2_path_warning_flags"
            + ", t.lr2_chart_path_cp932_bytes"
            + ", t.lr2_folder_scan_cp932_bytes"
            + ", t.lr2_resource_warning_flags"
            + ", t.lr2_resource_max_raw_cp932_bytes"
            + ", t.lr2_resource_max_resolved_cp932_bytes"
            + ", t.lr2_resource_unsupported_count "
            + "FROM " + tempName + " t "
            + "JOIN " + tableName + " m INDEXED BY " + BmsLibraryDbGateway.MaintenancePathNocaseIndexName
            + " ON m." + pathColumn + " = t.path COLLATE NOCASE "
            + "WHERE m." + pathColumn + " COLLATE NOCASE IN (SELECT path FROM " + tempName + ") "
            + "AND (" + changedPredicate + ");");
        int updated = songDb.Execute(
            "UPDATE " + tableName
            + " SET "
            + hashColumn + " = (SELECT t.hash FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + pathFlagsColumn + " = (SELECT t.lr2_path_warning_flags FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + chartPathBytesColumn + " = (SELECT t.lr2_chart_path_cp932_bytes FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + folderScanBytesColumn + " = (SELECT t.lr2_folder_scan_cp932_bytes FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + resourceFlagsColumn + " = (SELECT t.lr2_resource_warning_flags FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + maxRawBytesColumn + " = (SELECT t.lr2_resource_max_raw_cp932_bytes FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + maxResolvedBytesColumn + " = (SELECT t.lr2_resource_max_resolved_cp932_bytes FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + unsupportedCountColumn + " = (SELECT t.lr2_resource_unsupported_count FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid)"
            + " WHERE rowid IN (SELECT target_rowid FROM " + matchTempName + ");");

        int inserted = songDb.Execute(
            "INSERT INTO " + tableName
            + " (" + pathColumn
            + ", " + hashColumn
            + ", " + pathFlagsColumn
            + ", " + chartPathBytesColumn
            + ", " + folderScanBytesColumn
            + ", " + resourceFlagsColumn
            + ", " + maxRawBytesColumn
            + ", " + maxResolvedBytesColumn
            + ", " + unsupportedCountColumn
            + ") "
            + "SELECT t.path"
            + ", t.hash"
            + ", t.lr2_path_warning_flags"
            + ", t.lr2_chart_path_cp932_bytes"
            + ", t.lr2_folder_scan_cp932_bytes"
            + ", t.lr2_resource_warning_flags"
            + ", t.lr2_resource_max_raw_cp932_bytes"
            + ", t.lr2_resource_max_resolved_cp932_bytes"
            + ", t.lr2_resource_unsupported_count "
            + "FROM " + tempName + " t "
            + "WHERE NOT EXISTS (SELECT 1 FROM " + tableName + " m INDEXED BY " + BmsLibraryDbGateway.MaintenancePathNocaseIndexName
            + " WHERE m." + pathColumn + " = t.path COLLATE NOCASE);");
        return new Lr2CompatibilityFactsWriteResult(updated, inserted);
    }

    private static void PrepareTempLr2CompatibilityMaintenanceTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempLr2CompatibilityMaintenanceTable
            + " (path TEXT PRIMARY KEY COLLATE NOCASE, "
            + "hash TEXT, "
            + "lr2_path_warning_flags INTEGER, "
            + "lr2_chart_path_cp932_bytes INTEGER, "
            + "lr2_folder_scan_cp932_bytes INTEGER, "
            + "lr2_resource_warning_flags INTEGER, "
            + "lr2_resource_max_raw_cp932_bytes INTEGER, "
            + "lr2_resource_max_resolved_cp932_bytes INTEGER, "
            + "lr2_resource_unsupported_count INTEGER);");
        songDb.Execute("DELETE FROM temp." + TempLr2CompatibilityMaintenanceTable + ";");
    }

    private static void PrepareTempLr2CompatibilityMaintenanceMatchTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempLr2CompatibilityMaintenanceMatchTable
            + " (target_rowid INTEGER PRIMARY KEY, "
            + "hash TEXT, "
            + "lr2_path_warning_flags INTEGER, "
            + "lr2_chart_path_cp932_bytes INTEGER, "
            + "lr2_folder_scan_cp932_bytes INTEGER, "
            + "lr2_resource_warning_flags INTEGER, "
            + "lr2_resource_max_raw_cp932_bytes INTEGER, "
            + "lr2_resource_max_resolved_cp932_bytes INTEGER, "
            + "lr2_resource_unsupported_count INTEGER);");
        songDb.Execute("DELETE FROM temp." + TempLr2CompatibilityMaintenanceMatchTable + ";");
    }

    private static void BulkInsertLr2CompatibilityMaintenanceTempRows(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFileMaintenanceInfo> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        const int columnCount = 9;
        const int chunkSize = 100;
        for (int offset = 0; offset < rows.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, rows.Count - offset);
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", columnCount)) + ")";
            string placeholders = string.Join(",", Enumerable.Repeat(rowPlaceholders, count));
            var args = new List<object>(count * columnCount);
            for (int index = 0; index < count; index++)
            {
                BMSFileMaintenanceInfo row = rows[offset + index];
                args.Add(row.path);
                args.Add(row.hash);
                args.Add(row.lr2_path_warning_flags);
                args.Add(row.lr2_chart_path_cp932_bytes);
                args.Add(row.lr2_folder_scan_cp932_bytes);
                args.Add(row.lr2_resource_warning_flags);
                args.Add(row.lr2_resource_max_raw_cp932_bytes);
                args.Add(row.lr2_resource_max_resolved_cp932_bytes);
                args.Add(row.lr2_resource_unsupported_count);
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempLr2CompatibilityMaintenanceTable
                + " (path, hash, lr2_path_warning_flags, lr2_chart_path_cp932_bytes, lr2_folder_scan_cp932_bytes, "
                + "lr2_resource_warning_flags, lr2_resource_max_raw_cp932_bytes, lr2_resource_max_resolved_cp932_bytes, lr2_resource_unsupported_count) "
                + "VALUES " + placeholders + ";",
                args.ToArray());
        }
    }

    private static bool TryCreateLr2CompatibilityMaintenanceInfo(BMSFile row, out BMSFileMaintenanceInfo info)
    {
        info = null;
        if (row == null || string.IsNullOrWhiteSpace(row.path))
        {
            return false;
        }

        Lr2ChartPathEvaluation pathEvaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(row.path);
        Lr2ResourceReferenceEvaluation resourceEvaluation = Lr2CompatibilityEvaluator.EvaluateBmsResourceReferences(row.path, row);
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

    private sealed class SongRowSyncReadCandidate(
        int index,
        BMSFile existingSong,
        ChartFileReadBuffer buffer,
        long readElapsedTicks,
        bool transientSkipped = false)
    {
        public static SongRowSyncReadCandidate CreateTransientSkipped(int index, BMSFile existingSong)
        {
            return new SongRowSyncReadCandidate(index, existingSong, null, 0L, transientSkipped: true);
        }

        public int Index { get; } = index;

        public BMSFile ExistingSong { get; } = existingSong;

        public ChartFileReadBuffer Buffer { get; } = buffer;

        public long ReadElapsedTicks { get; } = readElapsedTicks;

        public bool TransientSkipped { get; } = transientSkipped;
    }

    private sealed class SongRowSyncComputedItem(
        int index,
        BMSFile row,
        bool parsedFromSnapshot,
        long readElapsedTicks,
        long digestElapsedTicks,
        long parseElapsedTicks,
        bool chartInfoApplied,
        long chartInfoElapsedTicks,
        LR2SongDBExtended.chart_info generatedChartInfoRow,
        LR2SongDBExtended.chart_info_parse_failure chartInfoParseFailureRow,
        string chartInfoParseFailureDeleteMd5,
        bool chartInfoParseFailureSkipped,
        BMSFileMaintenanceInfo lr2CompatibilityInfo,
        long compatibilityElapsedTicks,
        bool transientSkipped = false)
    {
        public static SongRowSyncComputedItem CreateTransientSkipped(int index)
        {
            return new SongRowSyncComputedItem(
                index,
                row: null,
                parsedFromSnapshot: false,
                readElapsedTicks: 0L,
                digestElapsedTicks: 0L,
                parseElapsedTicks: 0L,
                chartInfoApplied: false,
                chartInfoElapsedTicks: 0L,
                generatedChartInfoRow: null,
                chartInfoParseFailureRow: null,
                chartInfoParseFailureDeleteMd5: null,
                chartInfoParseFailureSkipped: false,
                lr2CompatibilityInfo: null,
                compatibilityElapsedTicks: 0L,
                transientSkipped: true);
        }

        public int Index { get; } = index;

        public BMSFile Row { get; } = row;

        public bool ParsedFromSnapshot { get; } = parsedFromSnapshot;

        public long ReadElapsedTicks { get; } = readElapsedTicks;

        public long DigestElapsedTicks { get; } = digestElapsedTicks;

        public long ParseElapsedTicks { get; } = parseElapsedTicks;

        public bool ChartInfoApplied { get; } = chartInfoApplied;

        public long ChartInfoElapsedTicks { get; } = chartInfoElapsedTicks;

        public LR2SongDBExtended.chart_info GeneratedChartInfoRow { get; } = generatedChartInfoRow;

        public LR2SongDBExtended.chart_info_parse_failure ChartInfoParseFailureRow { get; } = chartInfoParseFailureRow;

        public string ChartInfoParseFailureDeleteMd5 { get; } = chartInfoParseFailureDeleteMd5;

        public bool ChartInfoParseFailureSkipped { get; } = chartInfoParseFailureSkipped;

        public BMSFileMaintenanceInfo Lr2CompatibilityInfo { get; } = lr2CompatibilityInfo;

        public long CompatibilityElapsedTicks { get; } = compatibilityElapsedTicks;

        public bool TransientSkipped { get; } = transientSkipped;
    }

    private sealed class SongRowSyncResult(
        int processedCount,
        int skippedCount,
        int parseFailureCount,
        int chartInfoAppliedCount,
        int lr2CompatibilityAppliedCount)
    {
        public int ProcessedCount { get; } = processedCount;

        public int SkippedCount { get; } = skippedCount;

        public int ParseFailureCount { get; } = parseFailureCount;

        public int ChartInfoAppliedCount { get; } = chartInfoAppliedCount;

        public int Lr2CompatibilityAppliedCount { get; } = lr2CompatibilityAppliedCount;
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
