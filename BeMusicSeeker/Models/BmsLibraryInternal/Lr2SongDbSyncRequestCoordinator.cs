using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILr2SongDbSyncRequestHost
{
    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    bool IsShutdownRequested { get; }

    Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; }

    int GetLr2SongDbSyncMutationInProgress();

    Lr2SongDbSyncRuntimeSnapshot GetLr2SongDbSyncRuntimeSnapshot();

    bool TryReserveLr2SongDbSyncPreparation(bool requiresPreparation, out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot);

    Lr2SongDbSyncStatusSnapshot GetLr2SongDbSyncStatusSnapshot();

    LR2SongDBExtended OpenSongDb();

    IDisposable EnterLr2SongDbSyncCatalogMutationLease();

    void PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status);

    bool TrySkipForShutdown(string operation, string reason);

    void ClearLr2SongDbSyncPreparedDataSurface(string reason);

    void ApplyLr2SongDbSyncPreparedDataSurface(string reason, Lr2SongDbSyncPreparedDataSurface preparedSurface);

    void ClearLr2SongDbSyncPrepareReservation();

    bool TryBeginLr2SongDbSyncRequest(out int requestVersion);

    void CompleteLr2SongDbSyncRequest(int requestVersion, string stage);

    void FailLr2SongDbSyncRequest(int requestVersion, Lr2SongDbSyncStatusKind status, string stage, string message);

    void RunLr2SongDbSync(string reason, string signature, int requestVersion);

    CancellationToken GetLr2SongDbSyncCancellationToken();

    Lr2SongDbSyncInput CreateLr2SongDbSyncInput();

    TimeSpan CurrentChartInfoParseTimeout { get; }

    void ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail = null);

    void PublishLr2SongDbSyncPreflightStage(string stage, string reason, string runId);

    void LogLr2SongDbSyncPreflightStageDone(string stage, string reason, string runId, long elapsedMs);

    void EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason);

    Dictionary<string, BMSFile> CreateLr2SongDbSyncCompatibilityProjectionIndex();

    Func<BMSFile, LR2SongDBExtended.chart_info> CreateLr2SongDbSyncChartInfoResolverSnapshot();

    HashSet<string> CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason);

    void UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows);

    CatalogChartInfoWriteReceipt ApplyLr2SongDbSyncChartInfoWrite(
        LR2SongDBExtended songDb,
        CatalogChartInfoWriteRequest request);

    bool IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input);

    void UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress);

    int ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, BMSFile> bmsByPath,
        bool logSummary,
        bool dispatchPresentation);

    ISet<string> GetLr2SongDbSyncTransientSongRowsSkipPaths(Lr2SongDbSyncInput input, string reason);

    Lr2SongDbSyncSongRowsSkipVerificationResult VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFile> songRows,
        Lr2SongDbSyncInput input,
        string reason);

    void DispatchWarningPresentationChanged(string reason);

    void MarkLr2SongDbSyncPreflightCancelled(string signature, string runId, string stage);

    void MarkLr2SongDbSyncFailedStatus(string signature, string runId, Exception ex);

    void LogInstallPerformance(string message);
}

internal sealed class Lr2SongDbSyncRuntimeSnapshot
{
    public bool Running { get; set; }

    public bool Preparing { get; set; }

    public int MutationInProgress { get; set; }

    public int RequestedVersion { get; set; }

    public string Stage { get; set; } = string.Empty;

    public int ProcessedCount { get; set; }

    public int TotalCount { get; set; }

    public int StageProcessedCount { get; set; }

    public int StageTotalCount { get; set; }
}

internal static class Lr2SongDbSyncRequestCoordinator
{
    internal static Lr2SongDbSyncStatusSnapshot Queue(
        ILr2SongDbSyncRequestHost host,
        string reason,
        bool force,
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        bool allowIncompleteToQueue)
    {
        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        int mutationInProgress = host.GetLr2SongDbSyncMutationInProgress();
        if (mutationInProgress > 0)
        {
            host.LogInstallPerformance("lr2_song_db_sync queue_skipped reason=" + (reason ?? "unknown")
                + " mutationInProgress=" + mutationInProgress);
            return host.GetLr2SongDbSyncStatusSnapshot();
        }

        Lr2SongDbSyncStatusSnapshot status;
        using (LR2SongDBExtended songDb = host.OpenSongDb())
        {
            status = Lr2SongDbSyncStatusService.Evaluate(songDb, enabled, signature, DateTime.UtcNow);
        }
        host.PublishLr2SongDbSyncStatus(status);

        host.LogInstallPerformance("lr2_song_db_sync_status evaluate reason=" + (reason ?? "unknown")
            + " enabled=" + enabled.ToString().ToLowerInvariant()
            + " force=" + force.ToString().ToLowerInvariant()
            + " status=" + status.Status
            + " storedStatus=" + (status.StoredStatus?.ToString() ?? "(none)")
            + " signature=" + (status.Signature ?? string.Empty));

        if (host.TrySkipForShutdown("lr2_song_db_sync", reason))
        {
            return status;
        }

        if (!enabled
            || (!force && !status.IsNeeded)
            || (!force
                && !allowIncompleteToQueue
                && (status.Status == Lr2SongDbSyncStatusKind.Incomplete
                    || status.StoredStatus == Lr2SongDbSyncStatusKind.Incomplete)))
        {
            host.ClearLr2SongDbSyncPreparedDataSurface("queue_not_needed");
            return status;
        }

        bool prepareReserved = false;
        if (!host.TryReserveLr2SongDbSyncPreparation(prepareGeneratedData != null, out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot))
        {
            host.LogInstallPerformance("lr2_song_db_sync queue_skipped reason=" + (reason ?? "unknown")
                + " status=" + status.Status
                + " stage=" + (blockingSnapshot.Stage ?? string.Empty)
                + " requestedVersion=" + blockingSnapshot.RequestedVersion
                + " preparing=" + blockingSnapshot.Preparing.ToString().ToLowerInvariant()
                + " mutationInProgress=" + blockingSnapshot.MutationInProgress);
            if (blockingSnapshot.Running || blockingSnapshot.Preparing)
            {
                status.Status = Lr2SongDbSyncStatusKind.Running;
                status.Stage = blockingSnapshot.Stage;
                status.ProcessedCursor = blockingSnapshot.ProcessedCount;
                status.TotalCount = blockingSnapshot.TotalCount;
                status.StageProcessedCount = blockingSnapshot.StageProcessedCount;
                status.StageTotalCount = blockingSnapshot.StageTotalCount;
                host.PublishLr2SongDbSyncStatus(status);
            }
            return status;
        }
        prepareReserved = prepareGeneratedData != null;

        if (prepareGeneratedData != null)
        {
            var prepareStopwatch = Stopwatch.StartNew();
            try
            {
                host.LogInstallPerformance("lr2_song_db_sync prepare_start"
                    + " reason=" + (reason ?? "unknown"));
                Lr2SongDbSyncPreparedDataSurface preparedSurface = prepareGeneratedData();
                prepareStopwatch.Stop();
                host.LogInstallPerformance("lr2_song_db_sync prepare_done"
                    + " reason=" + (reason ?? "unknown")
                    + " scopeDirs=" + (preparedSurface?.Lr2FolderScopeDirectories?.Count ?? 0)
                    + " lr2FolderCandidates=" + (preparedSurface?.Lr2FolderFilePaths?.Count ?? 0)
                    + " directoryEntries=" + (preparedSurface?.DirectoryEntries?.Count ?? 0)
                    + " folderInfoCandidates=" + (preparedSurface?.FolderInfoFilePaths?.Count ?? 0)
                    + " textFileDirs=" + (preparedSurface?.TextFileDirectories?.Count ?? 0)
                    + " discoveryComplete=" + (preparedSurface?.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant() ?? "true")
                    + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds);
                host.ApplyLr2SongDbSyncPreparedDataSurface("prepare_generated_data", preparedSurface);
            }
            catch (Exception ex)
            {
                prepareStopwatch.Stop();
                host.ClearLr2SongDbSyncPreparedDataSurface("prepare_failed");
                host.LogInstallPerformance("lr2_song_db_sync prepare_failed reason=" + (reason ?? "unknown")
                    + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds
                    + " message=" + ex.Message);
                if (prepareReserved)
                {
                    host.ClearLr2SongDbSyncPrepareReservation();
                    prepareReserved = false;
                }
                throw;
            }
        }

        if (!host.TryBeginLr2SongDbSyncRequest(out int requestVersion))
        {
            host.ClearLr2SongDbSyncPreparedDataSurface("queue_skipped");
            if (prepareReserved)
            {
                host.ClearLr2SongDbSyncPrepareReservation();
                prepareReserved = false;
            }
            Lr2SongDbSyncRuntimeSnapshot runtimeSnapshot = host.GetLr2SongDbSyncRuntimeSnapshot();
            host.LogInstallPerformance("lr2_song_db_sync queue_skipped reason=" + (reason ?? "unknown")
                + " status=" + status.Status
                + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                + " requestedVersion=" + runtimeSnapshot.RequestedVersion);
            status.Status = Lr2SongDbSyncStatusKind.Running;
            status.Stage = runtimeSnapshot.Stage;
            status.ProcessedCursor = runtimeSnapshot.ProcessedCount;
            status.TotalCount = runtimeSnapshot.TotalCount;
            status.StageProcessedCount = runtimeSnapshot.StageProcessedCount;
            status.StageTotalCount = runtimeSnapshot.StageTotalCount;
            host.PublishLr2SongDbSyncStatus(status);
            return status;
        }
        if (prepareReserved)
        {
            host.ClearLr2SongDbSyncPrepareReservation();
            prepareReserved = false;
        }

        host.PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
            signature,
            stage: "queued",
            processedCursor: 0,
            totalCount: 0,
            lastError: null,
            stageProcessedCount: 0,
            stageTotalCount: 0));
        Task work()
        {
            host.RunLr2SongDbSync(reason, signature, requestVersion);
            return Task.CompletedTask;
        }
        if (host.StartupBackgroundTaskScheduler != null)
        {
            if (host.StartupBackgroundTaskScheduler("lr2_song_db_sync", reason ?? "queue", null, work))
            {
                return status;
            }
            host.CompleteLr2SongDbSyncRequest(requestVersion, "shutdown_skipped");
            host.LogInstallPerformance("lr2_song_db_sync skipped version=" + requestVersion + " reason=startup_scheduler_rejected");
            return status;
        }
        if (host.IsShutdownRequested)
        {
            host.CompleteLr2SongDbSyncRequest(requestVersion, "shutdown_skipped");
            host.LogInstallPerformance("lr2_song_db_sync skipped version=" + requestVersion + " reason=shutdown_requested");
            return status;
        }
        Task.Run(() => host.RunLr2SongDbSync(reason, signature, requestVersion)).Logging("Lr2SongDbSync");
        return status;
    }

    internal static void Run(
        ILr2SongDbSyncRequestHost host,
        string reason,
        string signature,
        int requestVersion)
    {
        var stopwatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N");
        CancellationToken cancellationToken = host.GetLr2SongDbSyncCancellationToken();
        bool enteredSyncService = false;
        int projectedCompatibilityWarningCount = 0;
        int committedChartInfoRowCount = 0;
        int committedChartInfoParseFailureChangeCount = 0;
        var committedCompatibilityFacts = new List<BMSFileMaintenanceInfo>();
        object committedCompatibilityFactsGate = new();
        bool compatibilityProjectionCompleted = false;
        Dictionary<string, BMSFile> compatibilityProjectionIndex = null;
        Action applyCommittedCompatibilityProjection = () =>
        {
            if (compatibilityProjectionCompleted)
            {
                return;
            }

            List<BMSFileMaintenanceInfo> compatibilityFactsSnapshot;
            lock (committedCompatibilityFactsGate)
            {
                compatibilityFactsSnapshot = [.. committedCompatibilityFacts];
            }
            if (compatibilityFactsSnapshot.Count > 0)
            {
                projectedCompatibilityWarningCount = host.ApplyLr2SongDbSyncCompatibilityProjection(
                    compatibilityFactsSnapshot,
                    reason,
                    compatibilityProjectionIndex,
                    logSummary: false,
                    dispatchPresentation: false);
            }
            compatibilityProjectionCompleted = true;
        };

        try
        {
            if (host.IsShutdownRequested || cancellationToken.IsCancellationRequested)
            {
                host.CompleteLr2SongDbSyncRequest(requestVersion, "shutdown_skipped");
                host.ReportStartupBackgroundTask("lr2_song_db_sync", "skipped", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_requested");
                host.LogInstallPerformance("lr2_song_db_sync skipped version=" + requestVersion + " reason=shutdown_requested");
                return;
            }
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "start", 0L, failed: false, detail: "runId=" + runId);
            host.PublishLr2SongDbSyncPreflightStage("chart_info_hydration", reason, runId);
            var preflightStageStopwatch = Stopwatch.StartNew();
            host.EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);
            host.LogLr2SongDbSyncPreflightStageDone("chart_info_hydration", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            host.PublishLr2SongDbSyncPreflightStage("input_surface", reason, runId);
            preflightStageStopwatch.Restart();
            Lr2SongDbSyncInput input = host.CreateLr2SongDbSyncInput();
            host.LogLr2SongDbSyncPreflightStageDone("input_surface", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            host.PublishLr2SongDbSyncPreflightStage("compatibility_projection_index", reason, runId);
            preflightStageStopwatch.Restart();
            compatibilityProjectionIndex = host.CreateLr2SongDbSyncCompatibilityProjectionIndex();
            host.LogLr2SongDbSyncPreflightStageDone("compatibility_projection_index", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            host.PublishLr2SongDbSyncPreflightStage("chart_info_resolver_snapshot", reason, runId);
            preflightStageStopwatch.Restart();
            Func<BMSFile, LR2SongDBExtended.chart_info> chartInfoResolver = host.CreateLr2SongDbSyncChartInfoResolverSnapshot();
            HashSet<string> currentChartInfoParseFailureMd5s = host.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);
            host.LogLr2SongDbSyncPreflightStageDone("chart_info_resolver_snapshot", reason, runId, preflightStageStopwatch.ElapsedMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();

            Lr2SongDbSyncResult result;
            using (host.EnterLr2SongDbSyncCatalogMutationLease())
            using (LR2SongDBExtended songDb = host.OpenSongDb())
            {
                enteredSyncService = true;
                result = Lr2SongDbSyncService.Run(songDb, new Lr2SongDbSyncRequest
                {
                    Signature = signature,
                    RunId = runId,
                    RootDirectories = input.RootDirectories,
                    ChartPaths = input.ChartPaths,
                    NormalFolderDirectoryPaths = input.NormalFolderDirectoryPaths,
                    FolderInfoFilePaths = input.FolderInfoFilePaths,
                    FolderInfoFileEntries = input.FolderInfoFileEntries,
                    DirectoryEntries = input.DirectoryEntries,
                    Lr2FolderFilePaths = input.Lr2FolderFilePaths,
                    Lr2FolderFileEntries = input.Lr2FolderFileEntries,
                    Lr2FolderDiscoveryDirectories = input.Lr2FolderDiscoveryDirectories,
                    Lr2FolderPruneDirectories = input.Lr2FolderPruneDirectories,
                    Lr2FolderPruneExcludedDirectories = input.Lr2FolderPruneExcludedDirectories,
                    Lr2FolderPruneExcludedPaths = input.Lr2FolderPruneExcludedPaths,
                    Lr2RootPath = input.Lr2RootPath,
                    Lr2NormalCustomFolderOutputBaseDir = input.Lr2NormalCustomFolderOutputBaseDir,
                    Lr2AdditionalNormalCustomFolderOutputBaseDirs = input.Lr2AdditionalNormalCustomFolderOutputBaseDirs,
                    Lr2RootCustomFolderOutputBaseDir = input.Lr2RootCustomFolderOutputBaseDir,
                    Lr2BuiltinFolderSourceDirectories = input.Lr2BuiltinFolderSourceDirectories,
                    Lr2FolderFileDiscoveryComplete = input.Lr2FolderFileDiscoveryComplete,
                    SongRows = input.SongRows,
                    TextFileDirectories = input.TextFileDirectories,
                    ChartInfoResolver = chartInfoResolver,
                    ChartInfoResolverIsThreadSafe = true,
                    ChartInfoParseTimeout = host.CurrentChartInfoParseTimeout,
                    CurrentChartInfoParseFailureMd5s = currentChartInfoParseFailureMd5s,
                    ChartInfoRowsCommitted = rows =>
                    {
                        int count = rows?.Count ?? 0;
                        if (count > 0)
                        {
                            host.UpsertLr2SongDbSyncChartInfoIndexRows(rows);
                            Interlocked.Add(ref committedChartInfoRowCount, count);
                        }
                    },
                    ChartInfoChunkWriter = (songDb, writeRequest) =>
                        host.ApplyLr2SongDbSyncChartInfoWrite(songDb, writeRequest),
                    ChartInfoParseFailuresCommitted = (persisted, cleared) =>
                    {
                        int count = Math.Max(0, persisted) + Math.Max(0, cleared);
                        if (count > 0)
                        {
                            Interlocked.Add(ref committedChartInfoParseFailureChangeCount, count);
                        }
                    },
                    StartedAtUtc = DateTime.UtcNow,
                    CancellationToken = cancellationToken,
                    IsSourceCurrent = () => host.IsLr2SongDbSyncInputCurrent(input),
                    ProgressReporter = host.UpdateLr2SongDbSyncProgress,
                    Lr2CompatibilityFactsCommitted = infos =>
                    {
                        if (infos == null || infos.Count == 0)
                        {
                            return;
                        }
                        lock (committedCompatibilityFactsGate)
                        {
                            committedCompatibilityFacts.AddRange(infos.Where(info => info != null));
                        }
                    },
                    TransientSongRowsSkipPaths = host.GetLr2SongDbSyncTransientSongRowsSkipPaths(input, reason),
                    SongRowsSkipVerifier = (songRowsSongDb, songRows) =>
                        host.VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
                            songRowsSongDb,
                            songRows,
                            input,
                            reason),
                    LogInstallPerformance = host.LogInstallPerformance
                });
            }
            applyCommittedCompatibilityProjection();
            stopwatch.Stop();
            bool completed = string.Equals(result.FinalStage, Lr2SongDbSyncService.CompletedStage, StringComparison.Ordinal);
            host.LogInstallPerformance("lr2_song_db_sync " + (completed ? "completed" : "incomplete")
                + " reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " roots=" + input.RootDirectories.Count
                + " charts=" + input.ChartPaths.Count
                + " normalFolderDirs=" + input.NormalFolderDirectoryPaths.Count
                + " folderInfoCandidates=" + input.FolderInfoFilePaths.Count
                + " lr2FolderRoots=" + input.Lr2FolderDiscoveryDirectories.Count
                + " lr2FolderPruneRoots=" + input.Lr2FolderPruneDirectories.Count
                + " lr2FolderPruneExcludedDirs=" + input.Lr2FolderPruneExcludedDirectories.Count
                + " lr2FolderCandidates=" + input.Lr2FolderFilePaths.Count
                + " lr2FolderDiscoveryComplete=" + input.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " textFileDirs=" + input.TextFileDirectories.Count
                + " songRows=" + input.SongRows.Count
                + " normalFolderGenerated=" + (result.NormalFolderSyncResult?.GeneratedCount ?? 0)
                + " normalFolderUpserted=" + (result.NormalFolderSyncResult?.UpsertedCount ?? 0)
                + " normalFolderDeleted=" + (result.NormalFolderSyncResult?.DeletedCount ?? 0)
                + " normalFolderSkippedUnsupported=" + (result.NormalFolderSyncResult?.SkippedUnsupportedPathCount ?? 0)
                + " normalFolderSkippedMissingMetadata=" + (result.NormalFolderSyncResult?.SkippedMissingMetadataCount ?? 0)
                + " normalFolderSkippedIncompatibleChart=" + (result.NormalFolderSyncResult?.SkippedIncompatibleChartPathCount ?? 0)
                + " normalFolderTargetBuildMs=" + (result.NormalFolderSyncResult?.TargetBuildMs ?? 0)
                + " normalFolderMetadataBuildMs=" + (result.NormalFolderSyncResult?.MetadataBuildMs ?? 0)
                + " normalFolderExistingReadMs=" + (result.NormalFolderSyncResult?.ExistingReadMs ?? 0)
                + " normalFolderRowGenerateMs=" + (result.NormalFolderSyncResult?.RowGenerateMs ?? 0)
                + " normalFolderPlanMs=" + (result.NormalFolderSyncResult?.PlanMs ?? 0)
                + " normalFolderWriteMs=" + (result.NormalFolderSyncResult?.WriteMs ?? 0)
                + " lr2FolderGenerated=" + (result.Lr2FolderFileSyncResult?.GeneratedCount ?? 0)
                + " lr2FolderUpserted=" + (result.Lr2FolderFileSyncResult?.UpsertedCount ?? 0)
                + " lr2FolderDeleted=" + (result.Lr2FolderFileSyncResult?.DeletedCount ?? 0)
                + " lr2FolderExistingRows=" + (result.Lr2FolderFileSyncResult?.ExistingReadCount ?? 0)
                + " lr2FolderSkippedUnsupported=" + (result.Lr2FolderFileSyncResult?.SkippedUnsupportedPathCount ?? 0)
                + " lr2FolderSkippedMissingMetadata=" + (result.Lr2FolderFileSyncResult?.SkippedMissingMetadataCount ?? 0)
                + " lr2FolderProcessed=" + result.Lr2FolderFileProcessedCount
                + " songRowProcessed=" + result.SongRowProcessedCount
                + " songRowSkipped=" + result.SongRowSkippedCount
                + " songRowParseFailed=" + result.SongRowParseFailureCount
                + " songRowChartInfoApplied=" + result.SongRowChartInfoAppliedCount
                + " songRowLr2CompatibilityApplied=" + result.SongRowLr2CompatibilityAppliedCount
                + " staleSongRowsPruned=" + result.StaleSongRowPrunedCount
                + " processed=" + result.ProcessedCount
                + " total=" + result.TotalCount
                + " startupScanBlockers=" + (result.StartupScanDiagnosticResult?.TotalBlockerCount ?? 0)
                + " startupScanNoRootSet=" + (result.StartupScanDiagnosticResult?.NoRootSetBlockerCount ?? 0)
                + " startupScanMissingSongRows=" + (result.StartupScanDiagnosticResult?.MissingCurrentSongRowCount ?? 0)
                + " startupScanDateMissingSongRows=" + (result.StartupScanDiagnosticResult?.DateMissingSongRowCount ?? 0)
                + " startupScanUnknownRootSongRows=" + (result.StartupScanDiagnosticResult?.UnknownRootSongRowCount ?? 0)
                + " startupScanDateMissingFolderRows=" + (result.StartupScanDiagnosticResult?.DateMissingFolderRowCount ?? 0)
                + " startupScanDateStaleFolderRows=" + (result.StartupScanDiagnosticResult?.DateStaleFolderRowCount ?? 0)
                + " startupScanUnknownRootFolderRows=" + (result.StartupScanDiagnosticResult?.UnknownRootFolderRowCount ?? 0)
                + " startupScanCleanupFolderRows=" + (result.StartupScanDiagnosticResult?.CleanupFolderRowCount ?? 0)
                + " startupScanFolderDateUpdates=" + (result.StartupScanDiagnosticResult?.FolderDateUpdateCount ?? 0)
                + " stage=" + result.FinalStage
                + " detail=" + (result.IncompleteReason ?? "completed")
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            host.LogInstallPerformance("lr2_song_db_sync_compatibility_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " input=" + result.SongRowLr2CompatibilityAppliedCount
                + " applied=" + projectedCompatibilityWarningCount);
            host.LogInstallPerformance("lr2_song_db_sync_chart_info_projection applied"
                + " reason=" + (reason ?? "unknown")
                + " rows=" + committedChartInfoRowCount
                + " parseFailureChanges=" + committedChartInfoParseFailureChangeCount);
            if (committedChartInfoRowCount > 0 || committedChartInfoParseFailureChangeCount > 0)
            {
                host.DispatchWarningPresentationChanged("lr2_song_db_sync_inline_chart_info");
            }
            if (projectedCompatibilityWarningCount > 0)
            {
                host.DispatchWarningPresentationChanged("lr2_song_db_sync_compatibility_projection");
            }
            if (completed)
            {
                host.CompleteLr2SongDbSyncRequest(requestVersion, result.FinalStage);
            }
            else
            {
                host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Incomplete, result.FinalStage, BuildLr2SongDbSyncIncompleteDetail(result));
            }
            host.ReportStartupBackgroundTask("lr2_song_db_sync", completed ? "done" : "incomplete", stopwatch.ElapsedMilliseconds, failed: false, detail: completed ? "completed" : BuildLr2SongDbSyncIncompleteDetail(result));
        }
        catch (OperationCanceledException)
        {
            try
            {
                applyCommittedCompatibilityProjection();
            }
            catch (Exception projectionException)
            {
                host.LogInstallPerformance("lr2_song_db_sync compatibility_projection_after_cancel_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " exception=" + projectionException.GetType().Name
                    + " message=" + projectionException.Message);
            }
            stopwatch.Stop();
            Lr2SongDbSyncRuntimeSnapshot runtimeSnapshot = host.GetLr2SongDbSyncRuntimeSnapshot();
            if (!enteredSyncService)
            {
                host.MarkLr2SongDbSyncPreflightCancelled(signature, runId, runtimeSnapshot.Stage);
            }
            host.LogInstallPerformance("lr2_song_db_sync cancelled reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                + " processed=" + runtimeSnapshot.ProcessedCount
                + " total=" + runtimeSnapshot.TotalCount);
            host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Cancelled, runtimeSnapshot.Stage, string.Empty);
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "cancelled", stopwatch.ElapsedMilliseconds, failed: false, detail: "cancelled");
        }
        catch (Exception ex)
        {
            try
            {
                applyCommittedCompatibilityProjection();
            }
            catch (Exception projectionException)
            {
                host.LogInstallPerformance("lr2_song_db_sync compatibility_projection_after_failure_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " exception=" + projectionException.GetType().Name
                    + " message=" + projectionException.Message);
            }
            stopwatch.Stop();
            try
            {
                host.MarkLr2SongDbSyncFailedStatus(signature, runId, ex);
            }
            catch
            {
                // Preserve the original failure in the startup task report.
            }
            host.LogInstallPerformance("lr2_song_db_sync failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " exception=" + ex.GetType().Name
                + " message=" + ex.Message);
            host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Failed, "failed", ex.Message);
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
        }
    }

    internal static bool TryRunDataPreparation(
        ILr2SongDbSyncRequestHost host,
        string reason,
        Func<Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData)
    {
        if (prepareGeneratedData == null)
        {
            return false;
        }

        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return false;
        }

        if (!host.TryReserveLr2SongDbSyncPreparation(requiresPreparation: true, out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot))
        {
            host.LogInstallPerformance("lr2_song_db_sync_data_prepare skipped reason=" + (reason ?? "unknown")
                + " stage=" + (blockingSnapshot.Stage ?? string.Empty)
                + " requestedVersion=" + blockingSnapshot.RequestedVersion
                + " preparing=" + blockingSnapshot.Preparing.ToString().ToLowerInvariant()
                + " mutationInProgress=" + blockingSnapshot.MutationInProgress);
            return false;
        }

        try
        {
            host.LogInstallPerformance("lr2_song_db_sync_data_prepare start reason=" + (reason ?? "unknown"));
            Lr2SongDbSyncPreparedDataSurface preparedSurface = prepareGeneratedData();
            host.ApplyLr2SongDbSyncPreparedDataSurface("prepare_generated_data", preparedSurface);
            host.LogInstallPerformance("lr2_song_db_sync_data_prepare done reason=" + (reason ?? "unknown"));
            return true;
        }
        catch (Exception ex)
        {
            host.LogInstallPerformance("lr2_song_db_sync_data_prepare failed reason=" + (reason ?? "unknown")
                + " message=" + ex.Message);
            throw;
        }
        finally
        {
            host.ClearLr2SongDbSyncPrepareReservation();
        }
    }

    internal static Lr2StartupScanBlockerCleanupResult CleanupStartupScanBlockerFolderRows(
        ILr2SongDbSyncRequestHost host,
        string reason)
    {
        if (host.GetLr2SongDbSyncRuntimeSnapshot().Running)
        {
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
        }

        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return null;
        }

        Lr2SongDbSyncInput input = host.CreateLr2SongDbSyncInput();
        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        Lr2StartupScanBlockerCleanupResult result;
        Lr2SongDbSyncStatusSnapshot status;
        using (LR2SongDBExtended songDb = host.OpenSongDb())
        {
            result = Lr2SongDbSyncService.CleanupStartupScanBlockerFolderRows(
                songDb,
                input.RootDirectories,
                input.Lr2FolderDiscoveryDirectories,
                input.SongRows,
                input.Lr2RootPath);
            status = Lr2SongDbSyncStatusService.Evaluate(songDb, enabled, signature, DateTime.UtcNow);
        }

        host.LogInstallPerformance("lr2_song_db_sync_startup_scan_blocker_cleanup reason=" + (reason ?? "unknown")
            + " deletedFolderRows=" + (result?.DeletedFolderRowCount ?? 0)
            + " beforeBlockers=" + (result?.DiagnosticBefore?.TotalBlockerCount ?? 0)
            + " beforeCleanupFolderRows=" + (result?.DiagnosticBefore?.CleanupFolderRowCount ?? 0)
            + " afterBlockers=" + (result?.DiagnosticAfter?.TotalBlockerCount ?? 0)
            + " signature=" + signature);
        host.PublishLr2SongDbSyncStatus(status);
        return result;
    }

    internal static void PublishExternalStageProgress(
        ILr2SongDbSyncRequestHost host,
        string stage,
        int processedCount,
        int totalCount,
        string detail)
    {
        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        if (!enabled)
        {
            return;
        }

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        int safeTotal = Math.Max(0, totalCount);
        int safeProcessed = Math.Max(0, Math.Min(Math.Max(0, processedCount), safeTotal));
        host.PublishLr2SongDbSyncStatus(CreateRuntimeLr2SongDbSyncStatus(
            Lr2SongDbSyncStatusKind.Running,
            signature,
            stage: stage,
            processedCursor: safeProcessed,
            totalCount: safeTotal,
            lastError: detail,
            stageProcessedCount: safeProcessed,
            stageTotalCount: safeTotal));
    }

    private static string BuildLr2SongDbSyncIncompleteDetail(Lr2SongDbSyncResult result)
    {
        if (result == null)
        {
            return string.Empty;
        }

        string reason = result.IncompleteReason ?? result.FinalStage ?? string.Empty;
        if (string.Equals(result.FinalStage, Lr2SongDbSyncService.StartupScanBlockersStage, StringComparison.Ordinal)
            && result.StartupScanDiagnosticResult != null)
        {
            return reason + " " + result.StartupScanDiagnosticResult.ToLogDetail();
        }
        return reason;
    }

    private static Lr2SongDbSyncStatusSnapshot CreateRuntimeLr2SongDbSyncStatus(
        Lr2SongDbSyncStatusKind status,
        string signature,
        string stage,
        int? processedCursor,
        int? totalCount,
        string lastError,
        int? stageProcessedCount = null,
        int? stageTotalCount = null)
    {
        return new Lr2SongDbSyncStatusSnapshot
        {
            Status = status,
            StoredStatus = status,
            Signature = signature ?? string.Empty,
            Stage = stage ?? string.Empty,
            ProcessedCursor = processedCursor,
            TotalCount = totalCount,
            StageProcessedCount = stageProcessedCount,
            StageTotalCount = stageTotalCount,
            LastError = lastError ?? string.Empty,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
