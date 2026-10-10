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

internal sealed class Lr2SongDbSyncRequest
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

    /// <summary>
    /// Optional physical metadata resolver used by complete folder preflight
    /// when a directory was materialized outside the enumeration surface.
    /// </summary>
    public Func<string, DateTime?> DirectoryLastWriteTimeUtcResolver { get; init; }

    /// <summary>
    /// Optional folderinfo reader used by complete folder preflight.  The
    /// normal route reads CP932 through the long-path filesystem adapter.
    /// </summary>
    public Func<string, IEnumerable<string>> FolderInfoLinesReader { get; init; }

    public IReadOnlyCollection<string> Lr2FolderFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> Lr2FolderFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Lr2FolderDiscoveryDirectories { get; set; } = [];

    public IReadOnlyCollection<string> Lr2FolderPruneDirectories { get; set; } = [];

    public string Lr2RootPath { get; set; }

    public string Lr2NormalCustomFolderOutputBaseDir { get; set; }

    public IReadOnlyCollection<string> Lr2AdditionalNormalCustomFolderOutputBaseDirs { get; set; } = [];

    public string Lr2RootCustomFolderOutputBaseDir { get; set; }

    public IReadOnlyCollection<string> Lr2BuiltinFolderSourceDirectories { get; set; } = [];

    public bool Lr2FolderFileDiscoveryComplete { get; set; } = true;

    public IReadOnlyCollection<ChartFile> SongRows { get; set; } = [];

    public IReadOnlyCollection<string> TextFileDirectories { get; set; } = [];

    public Func<ChartFile, BeMusicSeeker.Models.ChartDetails> ChartInfoResolver { get; init; }

    public bool ChartInfoResolverIsThreadSafe { get; init; }

    public TimeSpan? ChartInfoParseTimeout { get; set; }

    public ISet<string> CurrentChartInfoParseFailureMd5s { get; set; }

    /// <summary>
    /// Gets the single-read boundary used by the song-row reader stage. Tests can observe
    /// the contract without changing the worker, whose only chart input remains the buffer.
    /// </summary>
    public Func<string, ChartFileReadBuffer> ChartFileBufferReader { get; init; }

    public Action<IReadOnlyList<BeMusicSeeker.Models.ChartDetails>> ChartInfoRowsCommitted { get; init; }

    /// <summary>
    /// Applies chart-info facts inside the synchronizer's existing song database
    /// transaction. The catalog mutation owner installs this callback for the
    /// production route; direct service tests provide an equivalent scoped writer.
    /// </summary>
    public Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> ChartInfoChunkWriter { get; init; }

    public Action<int, int, IReadOnlyList<string>> ChartInfoParseFailuresCommitted { get; init; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Identifies the only cancellation source that is an expected lifecycle
    /// interruption.  Other cancellation requests are recorded as failures.
    /// </summary>
    public Func<bool> IsShutdownRequested { get; init; }


    /// <summary>段階内の実対象件数と保存済みカーソルを区別して通知します。通知失敗は同期結果に影響しません。</summary>
    public Action<Lr2SongDbSyncProgress> ProgressReporter { get; init; }

    public Action<IReadOnlyList<ResourceHealthMaintenanceSnapshot>> Lr2CompatibilityFactsCommitted { get; init; }

    /// <summary>
    /// 同じ親操作の直前のファイル差分で、生成行をDBへ確定したBMSパスです。
    /// 呼出元の変更不能な結果から直接渡し、楽曲行段階では読取り・解析・DB現行性照会を省きます。
    /// </summary>
    public IReadOnlySet<string> CommittedBmsPaths { get; init; }

    public Action<string> LogInstallPerformance { get; init; }
}

internal sealed class Lr2SongDbSyncProgress
{
    public int ProcessedCursor { get; set; }

    public int TotalCount { get; set; }

    public string Stage { get; set; } = string.Empty;

    public int StageProcessedCount { get; set; }

    public int StageTotalCount { get; set; }
}

internal sealed class Lr2SongDbSyncResult
{
    public int TotalCount { get; set; }

    public int ProcessedCount { get; set; }

    public string FinalStage { get; set; }

    public string IncompleteReason { get; set; }

    public Lr2NormalFolderDbSyncResult NormalFolderSyncResult { get; set; }

    public Lr2FolderFileDbSyncResult Lr2FolderFileSyncResult { get; set; }

    public Lr2FolderTableReconciliationResult FolderTableReconciliationResult { get; set; }

    public int Lr2FolderFileProcessedCount { get; set; }

    public int SongRowProcessedCount { get; set; }

    public int SongRowSkippedCount { get; set; }

    public int SongRowParseFailureCount { get; set; }

    public int SongRowChartInfoAppliedCount { get; set; }

    public int SongRowLr2CompatibilityAppliedCount { get; set; }

    public long ElapsedMs { get; set; }
}

internal static class Lr2SongDbSyncService
{
    private static readonly ChartInfoBuildService chartInfoBuildService = new();

    private const int SongRowSyncMaxWorkerDegree = 6;

    private const string TempLr2CompatibilityMaintenanceTable = "lr2_song_db_sync_compatibility_maintenance";

    private const string TempLr2CompatibilityMaintenanceMatchTable = "lr2_song_db_sync_compatibility_maintenance_match";

    internal const string CompletedStage = "completed";



    /// <summary>
    /// 完全なfolder投影の一括保存と既存のsong保存を実行し、実処理の段階・件数を通知します。
    /// 保存済みカーソルは観測用に維持し、準備の進捗や表示排出を同期成功の条件にしません。
    /// </summary>
    internal static Lr2SongDbSyncResult Run(
        LR2SongDBExtended songDb,
        Lr2SongDbSyncRequest request,
        Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> chartInfoChunkWriter = null)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2SongDbSyncRequest();
        ReportProgress(request, 0, 0, "input_preparation", 0, 0);
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
        List<ChartFile> songRows = [.. (request.SongRows ?? [])
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.Path))];
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
        // Every non-completed and forced request is a fresh reconciliation.
        // processed_cursor is commit-backed progress for observers only and
        // must never select a later input item on a subsequent run.
        const int freshStartCursor = 0;
        const string initialStage = "folder_projection_preparation";

        Lr2SongDbSyncStatusService.MarkRunning(
            songDb,
            request.Signature,
            request.RunId,
            totalCount,
            stage: initialStage,
            nowUtc: request.StartedAtUtc,
            processedCursor: freshStartCursor);
        LogSync(request, "lr2_song_db_sync input_summary"
            + " roots=" + roots.Count
            + " charts=" + chartPaths.Count
            + " normalFolderDirs=" + normalFolderDirectoryPaths.Count
            + " folderInfoCandidates=" + folderInfoFilePaths.Count
            + " lr2FolderCandidates=" + lr2FolderFilePaths.Count
            + " songRows=" + songRows.Count
            + " durableTotal=" + totalCount
            + " startCursor=" + freshStartCursor
            + " initialStage=" + initialStage);
        ReportProgress(request, freshStartCursor, totalCount, initialStage, 0, 0);
        ThrowIfCancellationRequested(songDb, request, freshStartCursor, totalCount, initialStage);

        Lr2NormalFolderDbSyncResult normalFolderResult = null;
        Lr2FolderFileDbSyncResult lr2FolderFileResult = null;
        Lr2FolderTableReconciliationResult folderTableResult = null;
        int normalFolderProcessedCount = 0;
        int lr2FolderFileProcessedCount = 0;
        int folderProcessedCount = 0;
        LogStage(request, "stage_start", "folder_reconciliation", normalFolderTargetCount + lr2FolderFilePaths.Count, 0, 0);
        folderTableResult = Lr2FolderTableReconciliationService.Reconcile(
            songDb,
            request,
            stageProgressReporter: (stage, stageProcessedCount, stageTotalCount) => ReportProgress(
                request,
                freshStartCursor,
                totalCount,
                stage,
                stageProcessedCount,
                stageTotalCount));
        normalFolderProcessedCount = normalFolderTargetCount;
        lr2FolderFileProcessedCount = lr2FolderFilePaths.Count;
        folderProcessedCount = normalFolderProcessedCount + lr2FolderFileProcessedCount;
        LogStage(
            request,
            "stage_done",
            "folder_reconciliation",
            normalFolderTargetCount + lr2FolderFilePaths.Count,
            normalFolderTargetCount + lr2FolderFilePaths.Count,
            folderProcessedCount);
        ThrowIfCancellationRequested(songDb, request, folderProcessedCount, totalCount, "folder_reconciliation_completed");

        Lr2SongDbSyncStatusService.UpdateCursor(
            songDb,
            request.Signature,
            request.RunId,
            processedCursor: folderProcessedCount,
            totalCount: totalCount,
            stage: "folder_reconciliation_completed",
            nowUtc: DateTime.UtcNow);
        if (folderTableResult.GeneratedCount > 0)
        {
            ReportProgress(
                request,
                folderProcessedCount,
                totalCount,
                "folder_reconciliation_completed",
                folderTableResult.GeneratedCount,
                folderTableResult.GeneratedCount);
        }

        if (freshStartCursor < songRowsEndCursor)
        {
            int songStageStart = Math.Max(0, freshStartCursor - lr2FolderEndCursor);
            int songStageProcessedCursor = folderProcessedCount + songStageStart;
            LogStage(request, "stage_start", "song_rows", songRows.Count, songStageStart, songStageProcessedCursor);
            Lr2SongDbSyncStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: songStageProcessedCursor,
                totalCount: totalCount,
                stage: "song_rows",
                nowUtc: DateTime.UtcNow);
        }
        ThrowIfCancellationRequested(songDb, request, Math.Max(folderProcessedCount, freshStartCursor), totalCount, "song_rows");

        int songRowStartIndex = Math.Max(0, freshStartCursor - lr2FolderEndCursor);
        SongRowSyncResult songRowResult;
        if (freshStartCursor >= songRowsEndCursor)
        {
            songRowResult = new SongRowSyncResult(0, 0, 0, 0, 0);
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
                request,
                chartInfoChunkWriter);
        }
        int processedCount = freshStartCursor >= songRowsEndCursor
            ? songRowsEndCursor
            : lr2FolderEndCursor + songRowStartIndex + songRowResult.ProcessedCount;
        ThrowIfCancellationRequested(songDb, request, processedCount, totalCount, "song_rows_completed");

        if (freshStartCursor < songRowsEndCursor)
        {
            Lr2SongDbSyncStatusService.UpdateCursor(
                songDb,
                request.Signature,
                request.RunId,
                processedCursor: processedCount,
                totalCount: totalCount,
                stage: "song_rows_completed",
                nowUtc: DateTime.UtcNow);
            if (songRows.Count > 0)
            {
                ReportProgress(request, processedCount, totalCount, "song_rows_completed", songRowStartIndex + songRowResult.ProcessedCount, songRows.Count);
            }
            LogStage(request, "stage_done", "song_rows", songRows.Count, songRowStartIndex + songRowResult.ProcessedCount, processedCount);
        }
        ReportProgress(request, processedCount, totalCount, "final_validation", 0, 0);
        string finalStage;
        string incompleteReason;
        ReportProgress(request, processedCount, totalCount, "sync_state_saving", 0, 0);
        Lr2SongDbSyncStatusService.MarkCompleted(
            songDb,
            request.Signature,
            request.RunId,
            totalCount,
            nowUtc: DateTime.UtcNow);
        ReportProgress(request, totalCount, totalCount, CompletedStage, totalCount, totalCount);
        finalStage = CompletedStage;
        incompleteReason = null;
        stopwatch.Stop();
        return new Lr2SongDbSyncResult
        {
            TotalCount = totalCount,
            ProcessedCount = processedCount,
            FinalStage = finalStage,
            IncompleteReason = incompleteReason,
            NormalFolderSyncResult = normalFolderResult,
            Lr2FolderFileSyncResult = lr2FolderFileResult,
            FolderTableReconciliationResult = folderTableResult,
            Lr2FolderFileProcessedCount = lr2FolderFileProcessedCount,
            SongRowProcessedCount = songRowResult.ProcessedCount,
            SongRowSkippedCount = songRowResult.SkippedCount,
            SongRowParseFailureCount = songRowResult.ParseFailureCount,
            SongRowChartInfoAppliedCount = songRowResult.ChartInfoAppliedCount,
            SongRowLr2CompatibilityAppliedCount = songRowResult.Lr2CompatibilityAppliedCount,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static void ThrowIfCancellationRequested(
        LR2SongDBExtended songDb,
        Lr2SongDbSyncRequest request,
        int processedCursor,
        int totalCount,
        string stage)
    {
        if (request?.CancellationToken.IsCancellationRequested != true)
        {
            return;
        }

        // Cancellation is classified by the request coordinator.  Keeping
        // status writes out of this shared checkpoint is important because a
        // transaction may still be open and shutdown must report Incomplete
        // only after that transaction has rolled back.
        ReportProgress(request, processedCursor, totalCount, stage ?? "cancelled", 0, 0);
        if (request?.IsShutdownRequested?.Invoke() != true)
        {
            Lr2SongDbSyncStatusService.MarkFailed(
                songDb,
                request?.Signature,
                request?.RunId,
                processedCursor,
                totalCount,
                stage ?? "cancelled",
                "Unexpected operation cancellation.",
                DateTime.UtcNow);
        }
        throw new OperationCanceledException(request.CancellationToken);
    }

    private static void ThrowIfShutdownRequested(Lr2SongDbSyncRequest request)
    {
        if (request?.IsShutdownRequested?.Invoke() == true)
        {
            throw new OperationCanceledException(request.CancellationToken);
        }
    }

    private static void ReportProgress(
        Lr2SongDbSyncRequest request,
        int processedCount,
        int totalCount,
        string stage,
        int stageProcessedCount,
        int stageTotalCount)
    {
        try
        {
            request?.ProgressReporter?.Invoke(new Lr2SongDbSyncProgress
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
        Lr2SongDbSyncRequest request,
        string action,
        string stage,
        int totalCount,
        int processedCount,
        int processedCursor)
    {
        LogSync(request, "lr2_song_db_sync " + action
            + " stage=" + (stage ?? string.Empty)
            + " processed=" + Math.Max(0, processedCount)
            + " total=" + Math.Max(0, totalCount)
            + " processedCursor=" + Math.Max(0, processedCursor));
    }

    private static void LogSync(Lr2SongDbSyncRequest request, string message)
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

    internal static Lr2FolderDirectoryMetadataSnapshot CreateLr2FolderParentDirectoryMetadataSnapshot(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        Lr2SongDbSyncRequest request)
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

    /// <summary>
    /// Creates the folder-file sync items and reports fixed-denominator preparation progress when requested.
    /// </summary>
    /// <param name="progressReporter">Optional best-effort per-item preparation progress reporter.</param>
    internal static Lr2FolderFileSyncItemsResult CreateLr2FolderFileSyncItems(
        IEnumerable<string> filePaths,
        Lr2SongDbSyncRequest request,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath = null,
        Func<string, LR2SongDB.folder> existingRowResolver = null,
        Action<int, int, string> progressReporter = null)
    {
        List<string> materializedFilePaths = [..
            (filePaths ?? [])
                .Where(filePath => !string.IsNullOrWhiteSpace(filePath))];
        var items = new List<Lr2FolderFileSyncItem>();
        bool hasReadFailures = false;
        int totalCount = materializedFilePaths.Count;
        int processedCount = 0;
        int parseTargetCount = 0;
        int parsedCount = 0;
        int unchangedCount = 0;
        foreach (string filePath in materializedFilePaths)
        {
            RootFileEnumerationEntry entry = ResolveEnumerationEntry(entriesByPath, filePath);
            Lr2FolderFileSyncItem item = CreateLr2FolderFileSyncItem(filePath, request, entry, existingRowResolver);
            if (item.LastWriteTimeUtc == null)
            {
                hasReadFailures = true;
            }
            if (item.PreserveExistingRowOnly)
            {
                unchangedCount++;
            }
            else
            {
                parseTargetCount++;
                if (item.Definition != null)
                {
                    parsedCount++;
                }
            }
            items.Add(item);
            processedCount++;
            ReportLr2FolderFileSyncProgress(progressReporter, totalCount, processedCount, filePath);
        }
        return new Lr2FolderFileSyncItemsResult(items, hasReadFailures, parseTargetCount, parsedCount, unchangedCount);
    }

    private static void ReportLr2FolderFileSyncProgress(
        Action<int, int, string> progressReporter,
        int totalCount,
        int processedCount,
        string currentPath)
    {
        if (progressReporter == null || totalCount <= 0)
        {
            return;
        }

        try
        {
            progressReporter(totalCount, processedCount, currentPath);
        }
        catch
        {
            // Progress observation is best effort and must not affect sync preparation.
        }
    }

    internal static IReadOnlyDictionary<string, LR2SongDB.folder> CreateExistingLr2FolderRowMap(
        LR2SongDBExtended songDb,
        Lr2SongDbSyncRequest request,
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
        Lr2SongDbSyncRequest request,
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

    internal static IReadOnlyCollection<string> CreateLr2FolderDirectoryRowScopeDirectories(Lr2SongDbSyncRequest request)
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

    internal static IReadOnlyCollection<string> CreateLr2FolderDirectoryRowGenerationScopeDirectories(Lr2SongDbSyncRequest request)
    {
        var candidates = new List<string>();
        foreach (string rootDirectory in request?.RootDirectories ?? [])
        {
            candidates.Add(CreateDirectoryRowGenerationBoundary(rootDirectory));
        }

        string normalOutputBase = NormalizeDirectoryPathOrNull(request?.Lr2NormalCustomFolderOutputBaseDir);
        if (!string.IsNullOrWhiteSpace(normalOutputBase))
        {
            candidates.Add(CreateDirectoryRowGenerationBoundary(normalOutputBase));
        }
        foreach (string additionalNormalOutputBase in request?.Lr2AdditionalNormalCustomFolderOutputBaseDirs ?? [])
        {
            string normalizedAdditionalNormalOutputBase = NormalizeDirectoryPathOrNull(additionalNormalOutputBase);
            if (!string.IsNullOrWhiteSpace(normalizedAdditionalNormalOutputBase))
            {
                candidates.Add(CreateDirectoryRowGenerationBoundary(normalizedAdditionalNormalOutputBase));
            }
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
        Lr2SongDbSyncRequest request,
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
                Definition = Lr2FolderFileProjection.ParseDefinition(LongPathFileSystem.ReadLines(filePath, Encoding.GetEncoding("shift_jis"))),
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

    /// <summary>確認済みファイルの同期入力と、同じ確認ループで取得した解析量です。</summary>
    internal sealed class Lr2FolderFileSyncItemsResult(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        bool hasReadFailures,
        int parseTargetCount,
        int parsedCount,
        int unchangedCount)
    {
        /// <summary>確認を終えた各ファイルの同期入力です。この件数が確認済みファイル数です。</summary>
        public IReadOnlyCollection<Lr2FolderFileSyncItem> Items { get; } = items ?? [];

        public bool HasReadFailures { get; } = hasReadFailures;

        /// <summary>既存行の時刻等で解析を省略できず、読込み・解析を試みたファイル数です。</summary>
        public int ParseTargetCount { get; } = parseTargetCount;

        /// <summary>定義の読込み・解析が成功したファイル数です。DB行の生成・変更数とは異なります。</summary>
        public int ParsedCount { get; } = parsedCount;

        /// <summary>既存行が同じため定義の解析を省略したファイル数です。親ディレクトリ行の変更不要を保証しません。</summary>
        public int UnchangedCount { get; } = unchangedCount;
    }

    private static SongRowSyncResult UpsertSongRows(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<ChartFile> songRows,
        ISet<string> textFileDirectories,
        int startIndex,
        int baseProcessedCursor,
        int totalCount,
        string signature,
        string runId,
        Lr2SongDbSyncRequest request,
        Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> chartInfoChunkWriter)
    {
        if (songRows == null || songRows.Count == 0)
        {
            return new SongRowSyncResult(0, 0, 0, 0, 0);
        }

        List<ChartFile> targetRows = [.. songRows.Where(song => song != null && !string.IsNullOrWhiteSpace(song.Path))];
        int safeStartIndex = Math.Max(0, startIndex);
        if (targetRows.Count == 0 || safeStartIndex >= targetRows.Count)
        {
            return new SongRowSyncResult(0, 0, 0, 0, 0);
        }

        ReportProgress(request, baseProcessedCursor + safeStartIndex, totalCount, "song_rows_preparation", 0, 0);
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
        int committedPathSkipped = 0;
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
        var generatedChartInfoBySha256 = new ConcurrentDictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
        var generatedChartInfoByMd5 = new ConcurrentDictionary<string, BeMusicSeeker.Models.ChartDetails>(StringComparer.OrdinalIgnoreCase);
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> baseChartInfoResolver =
            CreateSongRowSyncChartInfoResolver(
                request?.ChartInfoResolver,
                request?.ChartInfoResolverIsThreadSafe == true);
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoResolver = row =>
        {
            if (row == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(row.Sha256)
                && generatedChartInfoBySha256.TryGetValue(row.Sha256, out BeMusicSeeker.Models.ChartDetails bySha256))
            {
                Interlocked.Increment(ref chartInfoRunCacheHitCount);
                return bySha256;
            }
            if (!string.IsNullOrWhiteSpace(row.Md5)
                && generatedChartInfoByMd5.TryGetValue(row.Md5, out BeMusicSeeker.Models.ChartDetails byMd5))
            {
                Interlocked.Increment(ref chartInfoRunCacheHitCount);
                return byMd5;
            }
            return baseChartInfoResolver?.Invoke(row);
        };
        void CacheGeneratedChartInfo(BeMusicSeeker.Models.ChartDetails row)
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
        void LogChartInfoEvaluation(string message) => LogSync(request, message);
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
        IReadOnlySet<string> committedBmsPaths = safeStartIndex == 0
            ? request?.CommittedBmsPaths
            : null;
        int committedBmsPathCount = committedBmsPaths?.Count ?? 0;
        LogSync(request, "lr2_song_db_sync pipeline_start"
            + " stage=song_rows"
            + " startIndex=" + safeStartIndex
            + " targetCount=" + targetRows.Count
            + " committedBmsPaths=" + committedBmsPathCount
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
        ReportProgress(request, committedProcessedCursor, totalCount, "song_rows", safeStartIndex, targetRows.Count);

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
            LogSync(request, "lr2_song_db_sync chunk_start"
                + " stage=song_rows"
                + " offset=" + offset
                + " count=" + chunk.Count
                + " processedCursor=" + (baseProcessedCursor + offset));
            var rowsToWrite = new List<(ChartFile Chart, bool ParsedFromSnapshot)>(chunk.Count);
            long chunkReadTicks = 0L;
            long chunkDigestTicks = 0L;
            long chunkParseTicks = 0L;
            int chunkFallbackCount = 0;
            int chunkParseFailureCount = 0;
            int chunkCommittedPathSkippedCount = 0;
            foreach (SongRowSyncComputedItem item in chunk)
            {
                chunkReadTicks += item.ReadElapsedTicks;
                chunkDigestTicks += item.DigestElapsedTicks;
                chunkParseTicks += item.ParseElapsedTicks;
                if (item.CommittedPathSkipped)
                {
                    chunkCommittedPathSkippedCount++;
                    continue;
                }
                ChartFile row = item.Row;
                if (row == null || string.IsNullOrWhiteSpace(row.Path))
                {
                    continue;
                }
                if (!item.ParsedFromSnapshot)
                {
                    chunkParseFailureCount++;
                    chunkFallbackCount++;
                }
                rowsToWrite.Add((row, item.ParsedFromSnapshot));
            }
            totalReadTicks += chunkReadTicks;
            totalDigestTicks += chunkDigestTicks;
            totalParseTicks += chunkParseTicks;

            long chunkChartInfoTicks = 0L;
            long chunkCompatibilityTicks = 0L;
            int chunkChartInfoAppliedCount = 0;
            var chunkCompatibilityInfos = new List<ResourceHealthMaintenanceSnapshot>();
            var chunkChartInfoRows = new List<BeMusicSeeker.Models.ChartDetails>();
            var chunkChartInfoParseFailures = new List<BeMusicSeeker.Models.ChartParseFailure>();
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
                songWriteResult = Lr2SongDbWriter.UpdateGeneratedSongsForLr2SongDbSyncWithResult(songDb, rowsToWrite);
                stageStopwatch.Stop();
                songStageMs = stageStopwatch.ElapsedMilliseconds;

                stageStopwatch.Restart();
                CatalogChartInfoWriteRequest chartInfoWriteRequest = new(
                    chartInfoRows: chunkChartInfoRows,
                    parseFailureRows: chunkChartInfoParseFailures,
                    parseFailureDeleteMd5s: chunkChartInfoParseFailureDeleteMd5s);
                CatalogChartInfoWriteReceipt chartInfoWriteReceipt = CatalogChartInfoWriteReceipt.NotApplied;
                Func<CatalogChartInfoWriteRequest, CatalogChartInfoWriteReceipt> effectiveChartInfoChunkWriter =
                    chartInfoChunkWriter ?? request.ChartInfoChunkWriter;
                if (chartInfoWriteRequest.HasChanges)
                {
                    if (effectiveChartInfoChunkWriter == null)
                    {
                        throw new InvalidOperationException("LR2 chart-info synchronization requires a catalog mutation writer.");
                    }
                    chartInfoWriteReceipt = effectiveChartInfoChunkWriter(chartInfoWriteRequest);
                }
                if (chartInfoWriteRequest.HasChanges && !chartInfoWriteReceipt.Applied)
                {
                    throw new InvalidOperationException("LR2 chart-info persistence returned no receipt.");
                }
                stageStopwatch.Stop();
                chartInfoStageMs = stageStopwatch.ElapsedMilliseconds;

                stageStopwatch.Restart();
                compatibilityWriteResult = UpsertLr2CompatibilityFacts(songDb, chunkCompatibilityInfos);
                stageStopwatch.Stop();
                compatibilityStageMs = stageStopwatch.ElapsedMilliseconds;

                ThrowIfShutdownRequested(request);
                stageStopwatch.Restart();
                songDb.Commit();
                stageStopwatch.Stop();
                sqliteCommitMs = stageStopwatch.ElapsedMilliseconds;
                stopwatchCommit.Stop();
                ReportCommittedChartInfoRows(request, chunkChartInfoRows);
                ReportCommittedChartInfoParseFailures(
                    request,
                    chunkChartInfoParseFailures.Count,
                    chunkChartInfoParseFailureDeleteMd5s.Count,
                    [.. chunkChartInfoParseFailures.Select(value => value.md5).Concat(chunkChartInfoParseFailureDeleteMd5s)]);
                ReportCommittedLr2CompatibilityFacts(request, chunkCompatibilityInfos);
                chartInfoGeneratedCount += chunkChartInfoRows.Count;
                chartInfoParseFailurePersistedCount += chunkChartInfoParseFailures.Count;
                chartInfoParseFailureClearedCount += chunkChartInfoParseFailureDeleteMd5s.Count;
                chartInfoParseFailureSkippedCount += chunk.Count(item => item.ChartInfoParseFailureSkipped);
                compatibilityApplied += chunkCompatibilityInfos.Count;
                parseFailureCount += chunkParseFailureCount;
                chartInfoAppliedCount += chunkChartInfoAppliedCount;
                committedPathSkipped += chunkCommittedPathSkippedCount;
                processed += chunk.Count;
                int processedCursor = baseProcessedCursor + offset + chunk.Count;
                stageStopwatch.Restart();
                Lr2SongDbSyncStatusService.UpdateCursor(
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
                LogSync(request, "lr2_song_db_sync chunk_done"
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
                    + " committedPathSkipped=" + chunkCommittedPathSkippedCount
                    + " compatibilityApplied=" + chunkCompatibilityInfos.Count
                    + " processedCursor=" + processedCursor
                    + " managedBytes=" + GC.GetTotalMemory(false));
            }
            catch (Exception ex)
            {
                stopwatchCommit.Stop();
                Exception rollbackException = TryRollbackSongRowChunk(songDb);
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                Lr2SongDbSyncStatusService.MarkFailed(
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

                    bool windowSlotAcquired = false;
                    bool windowSlotTransferred = false;
                    try
                    {
                        // 採番を先に行うと、後続の結果だけで枠が埋まり、先頭の読み手と
                        // 順序回収を待つ書き手が循環待ちになる。採番済みの項目には必ず枠を持たせる。
                        orderingWindow.Wait(pipelineToken);
                        windowSlotAcquired = true;
                        if (Volatile.Read(ref writerFailed) != 0)
                        {
                            break;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        int index = Interlocked.Increment(ref nextReadIndex);
                        if (index >= targetRows.Count)
                        {
                            // 末尾の確認用に取得した枠も finally で返す。
                            break;
                        }

                        SongRowSyncReadCandidate candidate = ShouldSkipCommittedBmsPath(targetRows[index], committedBmsPaths)
                            ? SongRowSyncReadCandidate.CreateCommittedPathSkipped(index, targetRows[index])
                            : ReadSyncSongRowCandidate(
                                index,
                                targetRows[index],
                                request?.ChartFileBufferReader ?? ChartFileContentReader.ReadBuffer);
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
                            orderingWindow.Release();
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
                        request?.CurrentChartInfoParseFailureMd5s,
                        LogChartInfoEvaluation,
                        LogChartInfoEvaluation);
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
            Lr2SongDbSyncStatusService.MarkFailed(
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

        LogSync(request, "lr2_song_db_sync pipeline_done"
            + " stage=song_rows"
            + " processed=" + processed
            + " committedPathSkipped=" + committedPathSkipped
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
        return new SongRowSyncResult(processed, committedPathSkipped, parseFailureCount, chartInfoAppliedCount, compatibilityApplied);
    }

    private static void ReportCommittedLr2CompatibilityFacts(
        Lr2SongDbSyncRequest request,
        IReadOnlyList<ResourceHealthMaintenanceSnapshot> compatibilityInfos)
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
            LogSync(request, "lr2_song_db_sync compatibility_projection_callback_failed"
                + " count=" + compatibilityInfos.Count
                + " reason=" + QuoteLogValue(ex.Message ?? ex.GetType().Name));
        }
    }

    private static void ReportCommittedChartInfoRows(
        Lr2SongDbSyncRequest request,
        IReadOnlyList<BeMusicSeeker.Models.ChartDetails> chartInfoRows)
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
            LogSync(request, "lr2_song_db_sync chart_info_callback_failed"
                + " count=" + chartInfoRows.Count
                + " reason=" + QuoteLogValue(ex.Message ?? ex.GetType().Name));
        }
    }

    private static void ReportCommittedChartInfoParseFailures(
        Lr2SongDbSyncRequest request,
        int persistedCount,
        int clearedCount,
        IReadOnlyList<string> changedMd5s)
    {
        if (request?.ChartInfoParseFailuresCommitted == null
            || (persistedCount <= 0 && clearedCount <= 0))
        {
            return;
        }

        try
        {
            request.ChartInfoParseFailuresCommitted(persistedCount, clearedCount, changedMd5s);
        }
        catch (Exception ex)
        {
            LogSync(request, "lr2_song_db_sync chart_info_failure_callback_failed"
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

    private static SongRowSyncReadCandidate ReadSyncSongRowCandidate(
        int index,
        ChartFile existingSong,
        Func<string, ChartFileReadBuffer> readBuffer)
    {
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.Path))
        {
            return new SongRowSyncReadCandidate(index, existingSong, null, 0L);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            ChartFileReadBuffer buffer = readBuffer(existingSong.Path);
            stopwatch.Stop();
            return new SongRowSyncReadCandidate(index, existingSong, buffer, stopwatch.ElapsedTicks);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is DecoderFallbackException)
        {
            return new SongRowSyncReadCandidate(index, existingSong, null, 0L);
        }
    }

    private static bool ShouldSkipCommittedBmsPath(ChartFile row, IReadOnlySet<string> committedBmsPaths)
    {
        return row != null
            && !string.IsNullOrWhiteSpace(row.Path)
            && committedBmsPaths != null
            && committedBmsPaths.Contains(row.Path);
    }

    private static SongRowSyncComputedItem CreateSyncSongRowItem(
        SongRowSyncReadCandidate candidate,
        ISet<string> textFileDirectories,
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> chartInfoResolver,
        Action<BeMusicSeeker.Models.ChartDetails> generatedChartInfoAvailable,
        TimeSpan? chartInfoParseTimeout,
        ISet<string> currentChartInfoParseFailureMd5s,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarn)
    {
        if (candidate?.CommittedPathSkipped == true)
        {
            return SongRowSyncComputedItem.CreateCommittedPathSkipped(candidate.Index);
        }

        ChartFileSnapshot snapshot = CreateSyncSongRowSnapshot(candidate, out long digestTicks);
        ChartFile row = CreateSyncSongRow(
            candidate,
            snapshot,
            textFileDirectories,
            out bool parsedFromSnapshot,
            out long parseTicks);
        bool chartInfoApplied = false;
        long chartInfoTicks = 0L;
        BeMusicSeeker.Models.ChartDetails generatedChartInfoRow = null;
        BeMusicSeeker.Models.ChartParseFailure chartInfoParseFailureRow = null;
        string chartInfoParseFailureDeleteMd5 = null;
        bool chartInfoParseFailureSkipped = false;
        if (row != null)
        {
            long chartInfoStart = Stopwatch.GetTimestamp();
            ChartFile chart = row;
            ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(chart);
            BeMusicSeeker.Models.ChartDetails currentRow = chartInfoResolver?.Invoke(row);
            bool hasCurrentParseFailure = IsCurrentChartInfoParseFailure(
                row,
                candidate,
                snapshot,
                currentChartInfoParseFailureMd5s);
            ChartInfoBuildService.ChartInfoSnapshotBuildResult snapshotResult = chartInfoBuildService.EvaluateSnapshot(
                snapshot,
                target,
                currentRow,
                hasCurrentParseFailure,
                chartInfoParseTimeout,
                logInstallPerformance,
                logInstallPerformanceWarn);
            BeMusicSeeker.Models.ChartDetails chartInfo = snapshotResult.Row;
            generatedChartInfoRow = snapshotResult.ShouldPersistRow ? snapshotResult.Row : null;
            chartInfoParseFailureRow = snapshotResult.ParseFailureRow;
            chartInfoParseFailureDeleteMd5 = snapshotResult.ParseFailureDeleteMd5;
            chartInfoParseFailureSkipped = snapshotResult.SkippedPersistedFailure;
            if (generatedChartInfoRow != null)
            {
                generatedChartInfoAvailable?.Invoke(generatedChartInfoRow);
            }
            chartInfoApplied = chartInfo != null;
            if (chartInfo != null)
            {
                row = row with { ChartInfo = chartInfo, Level = chartInfo.level, LevelText = chartInfo.level?.ToString(System.Globalization.CultureInfo.InvariantCulture), Difficulty = chartInfo.difficulty };
            }

            row = row with { Difficulty = Lr2ChartInfoSongProjection.NormalizeDifficulty(row.Difficulty) };
            chartInfoTicks = Stopwatch.GetTimestamp() - chartInfoStart;
        }

        ResourceHealthMaintenanceSnapshot compatibilityInfo = null;
        long compatibilityTicks = 0L;
        if (row != null)
        {
            if (parsedFromSnapshot)
            {
                long compatibilityStart = Stopwatch.GetTimestamp();
                TryCreateLr2CompatibilityMaintenanceInfo(row, out compatibilityInfo);
                compatibilityTicks = Stopwatch.GetTimestamp() - compatibilityStart;
            }
            row = row with { Resources = null };
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
        ChartFile row,
        SongRowSyncReadCandidate candidate,
        ChartFileSnapshot snapshot,
        ISet<string> currentChartInfoParseFailureMd5s)
    {
        if (currentChartInfoParseFailureMd5s == null || currentChartInfoParseFailureMd5s.Count == 0)
        {
            return false;
        }

        string md5 = !string.IsNullOrWhiteSpace(row?.Md5)
            ? row.Md5
            : (!string.IsNullOrWhiteSpace(snapshot?.Md5)
                ? snapshot.Md5
                : candidate?.ExistingSong?.Md5);
        return !string.IsNullOrWhiteSpace(md5) && currentChartInfoParseFailureMd5s.Contains(md5);
    }

    private static ChartFile CreateSyncSongRow(
        SongRowSyncReadCandidate candidate,
        ChartFileSnapshot snapshot,
        ISet<string> textFileDirectories,
        out bool parsedFromSnapshot,
        out long parseTicks)
    {
        parsedFromSnapshot = false;
        parseTicks = 0L;
        ChartFile existingSong = candidate?.ExistingSong;
        if (existingSong == null || string.IsNullOrWhiteSpace(existingSong.Path))
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
            ChartFile parsed = BmsChartFileParser.ParseSnapshotWithEncodingDetection(snapshot) with
            {
                Token = existingSong.Token,
                Txt = ResolveTextGroupFlag(existingSong.Path, textFileDirectories, existingSong.Txt.GetValueOrDefault()),
                Date = Lr2SongRowEnricher.ToLr2UnixSeconds(snapshot.LastWriteTimeUtc),
                Favorite = existingSong.Favorite,
                AddDate = existingSong.AddDate,
                Tag = existingSong.Tag
            };
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

    private static ChartFile CreateFallbackSyncSongRow(ChartFile existingSong, ISet<string> textFileDirectories)
    {
        return existingSong == null ? null : existingSong with { Txt = ResolveTextGroupFlag(existingSong.Path, textFileDirectories, existingSong.Txt.GetValueOrDefault()) };
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

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateSongRowSyncChartInfoResolver(
        Func<ChartFile, BeMusicSeeker.Models.ChartDetails> requestResolver,
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

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoResolver(
        IReadOnlyDictionary<string, BeMusicSeeker.Models.ChartDetails> bySha256,
        IReadOnlyDictionary<string, BeMusicSeeker.Models.ChartDetails> byMd5)
    {
        return row =>
        {
            if (row == null)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(row.Sha256)
                && bySha256 != null
                && bySha256.TryGetValue(row.Sha256, out BeMusicSeeker.Models.ChartDetails bySha256Row))
            {
                return bySha256Row;
            }
            if (!string.IsNullOrWhiteSpace(row.Md5)
                && byMd5 != null
                && byMd5.TryGetValue(row.Md5, out BeMusicSeeker.Models.ChartDetails byMd5Row))
            {
                return byMd5Row;
            }
            return null;
        };
    }

    private sealed class Lr2CompatibilityFactsWriteResult(int updatedCount, int insertedCount)
    {
        internal static Lr2CompatibilityFactsWriteResult Empty { get; } = new(0, 0);

        internal int UpdatedCount { get; } = updatedCount;

        internal int InsertedCount { get; } = insertedCount;
    }

    private static Lr2CompatibilityFactsWriteResult UpsertLr2CompatibilityFacts(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<ResourceHealthMaintenanceSnapshot> infos)
    {
        if (songDb == null)
        {
            return Lr2CompatibilityFactsWriteResult.Empty;
        }
        List<ResourceHealthMaintenanceSnapshot> rows = [.. (infos ?? [])
            .Where(info => info != null && !string.IsNullOrWhiteSpace(info.Path))];
        if (rows.Count == 0)
        {
            return Lr2CompatibilityFactsWriteResult.Empty;
        }

        PrepareTempLr2CompatibilityMaintenanceTable(songDb);
        BulkInsertLr2CompatibilityMaintenanceTempRows(songDb, rows);

        string tableName = SQLiteTable<LR2SongDBExtended.maintenance>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.path);
        string hashColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.hash);
        string warningFlagsColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_warning_flags);
        string maxRelativeBytesColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_max_relative_cp932_bytes);
        string hasParentTraversalColumn = SQLiteTable<LR2SongDBExtended.maintenance>.GetColumnName(item => item.lr2_resource_has_parent_traversal);

        string tempName = "temp." + TempLr2CompatibilityMaintenanceTable;
        string matchTempName = "temp." + TempLr2CompatibilityMaintenanceMatchTable;
        string changedPredicate =
            "m." + hashColumn + " IS NOT t.hash"
            + " OR m." + warningFlagsColumn + " IS NOT t.lr2_warning_flags"
            + " OR m." + maxRelativeBytesColumn + " IS NOT t.lr2_resource_max_relative_cp932_bytes"
            + " OR m." + hasParentTraversalColumn + " IS NOT t.lr2_resource_has_parent_traversal";
        PrepareTempLr2CompatibilityMaintenanceMatchTable(songDb);
        songDb.Execute(
            "INSERT OR REPLACE INTO " + matchTempName
            + " (target_rowid, hash, lr2_warning_flags, lr2_resource_max_relative_cp932_bytes, lr2_resource_has_parent_traversal) "
            + "SELECT m.rowid"
            + ", t.hash"
            + ", t.lr2_warning_flags"
            + ", t.lr2_resource_max_relative_cp932_bytes"
            + ", t.lr2_resource_has_parent_traversal "
            + "FROM " + tempName + " t "
            + "JOIN " + tableName + " m ON m." + pathColumn + " = t.path "
            + "WHERE m." + pathColumn + " IN (SELECT path FROM " + tempName + ") "
            + "AND (" + changedPredicate + ");");
        int updated = songDb.Execute(
            "UPDATE " + tableName
            + " SET "
            + hashColumn + " = (SELECT t.hash FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + warningFlagsColumn + " = (SELECT t.lr2_warning_flags FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + maxRelativeBytesColumn + " = (SELECT t.lr2_resource_max_relative_cp932_bytes FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid), "
            + hasParentTraversalColumn + " = (SELECT t.lr2_resource_has_parent_traversal FROM " + matchTempName + " t WHERE t.target_rowid = " + tableName + ".rowid)"
            + " WHERE rowid IN (SELECT target_rowid FROM " + matchTempName + ");");

        int inserted = songDb.Execute(
            "INSERT INTO " + tableName
            + " (" + pathColumn
            + ", " + hashColumn
            + ", " + warningFlagsColumn
            + ", " + maxRelativeBytesColumn
            + ", " + hasParentTraversalColumn
            + ") "
            + "SELECT t.path"
            + ", t.hash"
            + ", t.lr2_warning_flags"
            + ", t.lr2_resource_max_relative_cp932_bytes"
            + ", t.lr2_resource_has_parent_traversal "
            + "FROM " + tempName + " t "
            + "WHERE NOT EXISTS (SELECT 1 FROM " + tableName + " m"
            + " WHERE m." + pathColumn + " = t.path);");
        return new Lr2CompatibilityFactsWriteResult(updated, inserted);
    }

    private static void PrepareTempLr2CompatibilityMaintenanceTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempLr2CompatibilityMaintenanceTable
            + " (path TEXT PRIMARY KEY, "
            + "hash TEXT, "
            + "lr2_warning_flags INTEGER, "
            + "lr2_resource_max_relative_cp932_bytes INTEGER, "
            + "lr2_resource_has_parent_traversal INTEGER);");
        songDb.Execute("DELETE FROM temp." + TempLr2CompatibilityMaintenanceTable + ";");
    }

    private static void PrepareTempLr2CompatibilityMaintenanceMatchTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS temp." + TempLr2CompatibilityMaintenanceMatchTable
            + " (target_rowid INTEGER PRIMARY KEY, "
            + "hash TEXT, "
            + "lr2_warning_flags INTEGER, "
            + "lr2_resource_max_relative_cp932_bytes INTEGER, "
            + "lr2_resource_has_parent_traversal INTEGER);");
        songDb.Execute("DELETE FROM temp." + TempLr2CompatibilityMaintenanceMatchTable + ";");
    }

    private static void BulkInsertLr2CompatibilityMaintenanceTempRows(
        LR2SongDBExtended songDb,
        IReadOnlyList<ResourceHealthMaintenanceSnapshot> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        const int columnCount = 5;
        const int chunkSize = 100;
        for (int offset = 0; offset < rows.Count; offset += chunkSize)
        {
            int count = Math.Min(chunkSize, rows.Count - offset);
            string rowPlaceholders = "(" + string.Join(",", Enumerable.Repeat("?", columnCount)) + ")";
            string placeholders = string.Join(",", Enumerable.Repeat(rowPlaceholders, count));
            var args = new List<object>(count * columnCount);
            for (int index = 0; index < count; index++)
            {
                ResourceHealthMaintenanceSnapshot row = rows[offset + index];
                args.Add(row.Path);
                args.Add(row.Hash);
                args.Add(row.Lr2WarningFlags);
                args.Add(row.Lr2ResourceMaxRelativeCp932Bytes);
                args.Add(ToNullableInteger(row.Lr2ResourceHasParentTraversal));
            }
            songDb.Execute(
                "INSERT OR REPLACE INTO temp." + TempLr2CompatibilityMaintenanceTable
                + " (path, hash, lr2_warning_flags, lr2_resource_max_relative_cp932_bytes, lr2_resource_has_parent_traversal) "
                + "VALUES " + placeholders + ";",
                args.ToArray());
        }
    }

    private static bool TryCreateLr2CompatibilityMaintenanceInfo(ChartFile row, out ResourceHealthMaintenanceSnapshot info)
    {
        info = null;
        if (row == null || string.IsNullOrWhiteSpace(row.Path))
        {
            return false;
        }

        Lr2ChartPathEvaluation pathEvaluation = Lr2CompatibilityEvaluator.EvaluateChartPath(row.Path);
        Lr2ResourceReferenceEvaluation resourceEvaluation = Lr2CompatibilityEvaluator.EvaluateResourceReferences(row.Path, ChartResourceSnapshot.Create(row.Resources));
        info = new ResourceHealthMaintenanceSnapshot
        {
            Path = row.Path,
            Hash = row.Md5,
            Origin = MaintenanceInfoOrigin.Calculated,
            Lr2WarningFlags = (int)(pathEvaluation.WarningFlags | resourceEvaluation.WarningFlags),
            Lr2ResourceMaxRelativeCp932Bytes = resourceEvaluation.MaxRelativeCp932Bytes,
            Lr2ResourceHasParentTraversal = resourceEvaluation.HasParentTraversal
        };
        return true;
    }

    private static int? ToNullableInteger(bool? value)
    {
        return value.HasValue ? (value.Value ? 1 : 0) : null;
    }

    private sealed class SongRowSyncReadCandidate(
        int index,
        ChartFile existingSong,
        ChartFileReadBuffer buffer,
        long readElapsedTicks,
        bool committedPathSkipped = false)
    {
        public static SongRowSyncReadCandidate CreateCommittedPathSkipped(int index, ChartFile existingSong)
        {
            return new SongRowSyncReadCandidate(index, existingSong, null, 0L, committedPathSkipped: true);
        }

        public int Index { get; } = index;

        public ChartFile ExistingSong { get; } = existingSong;

        public ChartFileReadBuffer Buffer { get; } = buffer;

        public long ReadElapsedTicks { get; } = readElapsedTicks;

        public bool CommittedPathSkipped { get; } = committedPathSkipped;
    }

    private sealed class SongRowSyncComputedItem(
        int index,
        ChartFile row,
        bool parsedFromSnapshot,
        long readElapsedTicks,
        long digestElapsedTicks,
        long parseElapsedTicks,
        bool chartInfoApplied,
        long chartInfoElapsedTicks,
        BeMusicSeeker.Models.ChartDetails generatedChartInfoRow,
        BeMusicSeeker.Models.ChartParseFailure chartInfoParseFailureRow,
        string chartInfoParseFailureDeleteMd5,
        bool chartInfoParseFailureSkipped,
        ResourceHealthMaintenanceSnapshot lr2CompatibilityInfo,
        long compatibilityElapsedTicks,
        bool committedPathSkipped = false)
    {
        public static SongRowSyncComputedItem CreateCommittedPathSkipped(int index)
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
                committedPathSkipped: true);
        }

        public int Index { get; } = index;

        public ChartFile Row { get; } = row;

        public bool ParsedFromSnapshot { get; } = parsedFromSnapshot;

        public long ReadElapsedTicks { get; } = readElapsedTicks;

        public long DigestElapsedTicks { get; } = digestElapsedTicks;

        public long ParseElapsedTicks { get; } = parseElapsedTicks;

        public bool ChartInfoApplied { get; } = chartInfoApplied;

        public long ChartInfoElapsedTicks { get; } = chartInfoElapsedTicks;

        public BeMusicSeeker.Models.ChartDetails GeneratedChartInfoRow { get; } = generatedChartInfoRow;

        public BeMusicSeeker.Models.ChartParseFailure ChartInfoParseFailureRow { get; } = chartInfoParseFailureRow;

        public string ChartInfoParseFailureDeleteMd5 { get; } = chartInfoParseFailureDeleteMd5;

        public bool ChartInfoParseFailureSkipped { get; } = chartInfoParseFailureSkipped;

        public ResourceHealthMaintenanceSnapshot Lr2CompatibilityInfo { get; } = lr2CompatibilityInfo;

        public long CompatibilityElapsedTicks { get; } = compatibilityElapsedTicks;

        public bool CommittedPathSkipped { get; } = committedPathSkipped;
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

}
