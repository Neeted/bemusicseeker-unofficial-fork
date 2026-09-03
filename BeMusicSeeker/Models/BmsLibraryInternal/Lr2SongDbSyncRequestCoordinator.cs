using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

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
        BMSLibrary.Lr2SynchronizationOwner host,
        string reason,
        bool force,
        Func<LibraryFileMutationLease, Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData,
        bool allowIncompleteToQueue,
        bool allowCommittedPathReceipt)
    {
        bool receiptEligible = allowCommittedPathReceipt && !force;
        if (!receiptEligible)
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_origin_not_eligible");
        }
        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        bool enabled = options.OperationModeLR2DB;
        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
        Lr2SongDbSyncRuntimeSnapshot incumbent = host.GetLr2SongDbSyncRuntimeSnapshot();
        if (incumbent.MutationInProgress > 0 || incumbent.Running || incumbent.Preparing)
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_busy");
            host.LogInstallPerformance("lr2_song_db_sync queue_skipped reason=" + (reason ?? "unknown")
                + " mutationInProgress=" + incumbent.MutationInProgress
                + " running=" + incumbent.Running.ToString().ToLowerInvariant()
                + " preparing=" + incumbent.Preparing.ToString().ToLowerInvariant());
            return host.GetLr2SongDbSyncStatusSnapshot();
        }

        Lr2SongDbSyncStatusSnapshot status = host.EvaluateLr2SongDbSyncStatus(
            enabled,
            signature,
            DateTime.UtcNow);
        host.PublishLr2SongDbSyncStatus(status);

        host.LogInstallPerformance("lr2_song_db_sync_status evaluate reason=" + (reason ?? "unknown")
            + " enabled=" + enabled.ToString().ToLowerInvariant()
            + " force=" + force.ToString().ToLowerInvariant()
            + " status=" + status.Status
            + " storedStatus=" + (status.StoredStatus?.ToString() ?? "(none)")
            + " signature=" + (status.Signature ?? string.Empty));

        if (host.TrySkipForShutdown("lr2_song_db_sync", reason))
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("shutdown_requested");
            return status;
        }

        if (!enabled
            || (!force && !status.IsNeeded)
            || (!force
                && !allowIncompleteToQueue
                && (status.Status == Lr2SongDbSyncStatusKind.Incomplete
                    || status.StoredStatus == Lr2SongDbSyncStatusKind.Incomplete)))
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_not_needed");
            host.ClearLr2SongDbSyncPreparedDataSurface("queue_not_needed");
            return status;
        }

        if (!host.TryReserveLr2SongDbSyncPreparation(
            prepareGeneratedData != null,
            out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot,
            out LibraryFileMutationLease preparationLease))
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_busy");
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
        if (prepareGeneratedData != null)
        {
            var prepareStopwatch = Stopwatch.StartNew();
            Lr2SongDbSyncPreparedDataSurface preparedSurface = null;
            try
            {
                host.LogInstallPerformance("lr2_song_db_sync prepare_start"
                    + " reason=" + (reason ?? "unknown"));
                using (preparationLease)
                {
                    preparedSurface = prepareGeneratedData(preparationLease);
                    prepareStopwatch.Stop();
                }
                // The canonical prepared surface is published only after
                // physical output materialization and verification have
                // completed and the preparation lease has released normally;
                // LR2 folder-row persistence remains part of reconciliation.
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
                host.DiscardLr2SongDbSyncCommittedPathReceipt("prepare_failed");
                host.ClearLr2SongDbSyncPreparedDataSurface("prepare_failed");
                host.LogInstallPerformance("lr2_song_db_sync prepare_failed reason=" + (reason ?? "unknown")
                    + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds
                    + " message=" + ex.Message);
                preparationLease?.Dispose();
                throw;
            }
        }

        if (!host.TryBeginLr2SongDbSyncRequest(out int requestVersion))
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("queue_begin_rejected");
            host.ClearLr2SongDbSyncPreparedDataSurface("queue_skipped");
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
            host.RunLr2SongDbSync(reason, signature, requestVersion, receiptEligible);
            return Task.CompletedTask;
        }
        if (host.StartupBackgroundTaskScheduler != null)
        {
            if (host.StartupBackgroundTaskScheduler("lr2_song_db_sync", reason ?? "queue", null, work))
            {
                return status;
            }
            host.CompleteLr2SongDbSyncRequest(requestVersion, "shutdown_skipped");
            host.DiscardLr2SongDbSyncCommittedPathReceipt("startup_scheduler_rejected");
            host.LogInstallPerformance("lr2_song_db_sync skipped version=" + requestVersion + " reason=startup_scheduler_rejected");
            return status;
        }
        if (host.IsShutdownRequested)
        {
            host.CompleteLr2SongDbSyncRequest(requestVersion, "shutdown_skipped");
            host.DiscardLr2SongDbSyncCommittedPathReceipt("shutdown_requested");
            host.LogInstallPerformance("lr2_song_db_sync skipped version=" + requestVersion + " reason=shutdown_requested");
            return status;
        }
        Task.Run(() => host.RunLr2SongDbSync(reason, signature, requestVersion, receiptEligible)).Logging("Lr2SongDbSync");
        return status;
    }

    internal static void Run(
        BMSLibrary.Lr2SynchronizationOwner host,
        string reason,
        string signature,
        int requestVersion,
        bool allowCommittedPathReceipt)
    {
        var stopwatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N");
        CancellationToken cancellationToken = host.GetLr2SongDbSyncCancellationToken();
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
                host.DiscardLr2SongDbSyncCommittedPathReceipt("shutdown_requested");
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
            Lr2SongDbSyncCommittedPathReceipt committedPathReceipt = allowCommittedPathReceipt
                ? host.TakeLr2SongDbSyncCommittedPathReceipt(input, reason)
                : null;

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
            result = host.ApplyLr2SongDbSync(new Lr2SongDbSyncRequest
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
                IsShutdownRequested = () => host.IsShutdownRequested,
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
                CommittedPathReceipt = committedPathReceipt,
                LogInstallPerformance = host.LogInstallPerformance
            });
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
                + " lr2FolderCandidates=" + input.Lr2FolderFilePaths.Count
                + " lr2FolderDiscoveryComplete=" + input.Lr2FolderFileDiscoveryComplete.ToString().ToLowerInvariant()
                + " textFileDirs=" + input.TextFileDirectories.Count
                + " songRows=" + input.SongRows.Count
                + " folderTableGenerated=" + (result.FolderTableReconciliationResult?.GeneratedCount ?? 0)
                + " folderTableUpserted=" + (result.FolderTableReconciliationResult?.UpsertedCount ?? 0)
                + " folderTableDeleted=" + (result.FolderTableReconciliationResult?.DeletedCount ?? 0)
                + " folderTableExistingRows=" + (result.FolderTableReconciliationResult?.ExistingRowCount ?? 0)
                + " lr2FolderProcessed=" + result.Lr2FolderFileProcessedCount
                + " songRowProcessed=" + result.SongRowProcessedCount
                + " songRowSkipped=" + result.SongRowSkippedCount
                + " songRowParseFailed=" + result.SongRowParseFailureCount
                + " songRowChartInfoApplied=" + result.SongRowChartInfoAppliedCount
                + " songRowLr2CompatibilityApplied=" + result.SongRowLr2CompatibilityAppliedCount
                + " processed=" + result.ProcessedCount
                + " total=" + result.TotalCount
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
        catch (OperationCanceledException cancellationException)
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("sync_cancelled");
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
            bool shutdownInterruption = host.IsShutdownRequested;
            if (shutdownInterruption)
            {
                host.MarkLr2SongDbSyncInterruptedStatus(
                    signature,
                    runId,
                    runtimeSnapshot.ProcessedCount,
                    runtimeSnapshot.TotalCount,
                    runtimeSnapshot.Stage);
                host.LogInstallPerformance("lr2_song_db_sync interrupted reason=" + (reason ?? "unknown")
                    + " runId=" + runId
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                    + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                    + " processed=" + runtimeSnapshot.ProcessedCount
                    + " total=" + runtimeSnapshot.TotalCount);
                host.FailLr2SongDbSyncRequest(
                    requestVersion,
                    Lr2SongDbSyncStatusKind.Incomplete,
                    runtimeSnapshot.Stage,
                    "shutdown_interrupted");
                host.ReportStartupBackgroundTask("lr2_song_db_sync", "incomplete", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_interrupted");
                return;
            }
            host.MarkLr2SongDbSyncFailedStatus(signature, runId, cancellationException);
            host.LogInstallPerformance("lr2_song_db_sync cancellation_failed reason=" + (reason ?? "unknown")
                + " runId=" + runId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " stage=" + (runtimeSnapshot.Stage ?? string.Empty)
                + " processed=" + runtimeSnapshot.ProcessedCount
                + " total=" + runtimeSnapshot.TotalCount);
            host.FailLr2SongDbSyncRequest(requestVersion, Lr2SongDbSyncStatusKind.Failed, runtimeSnapshot.Stage, cancellationException.Message);
            host.ReportStartupBackgroundTask("lr2_song_db_sync", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: cancellationException.Message);
        }
        catch (Exception ex)
        {
            host.DiscardLr2SongDbSyncCommittedPathReceipt("sync_failed");
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
        BMSLibrary.Lr2SynchronizationOwner host,
        string reason,
        Func<LibraryFileMutationLease, Lr2SongDbSyncPreparedDataSurface> prepareGeneratedData)
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

        if (!host.TryReserveLr2SongDbSyncPreparation(
            requiresPreparation: true,
            out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot,
            out LibraryFileMutationLease preparationLease))
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
            Lr2SongDbSyncPreparedDataSurface preparedSurface;
            using (preparationLease)
            {
                preparedSurface = prepareGeneratedData(preparationLease);
            }
            // Do not expose a prepared surface while its owning capability is
            // still live; a terminal callback may immediately re-enter.
            host.ApplyLr2SongDbSyncPreparedDataSurface("prepare_generated_data", preparedSurface);
            host.LogInstallPerformance("lr2_song_db_sync_data_prepare done reason=" + (reason ?? "unknown"));
            return true;
        }
        catch (Exception ex)
        {
            // A failed standalone preparation must not leave the previous
            // prepared surface eligible for the next synchronization run.
            host.DiscardLr2SongDbSyncCommittedPathReceipt("prepare_failed");
            host.ClearLr2SongDbSyncPreparedDataSurface("prepare_failed");
            host.LogInstallPerformance("lr2_song_db_sync_data_prepare failed reason=" + (reason ?? "unknown")
                + " message=" + ex.Message);
            throw;
        }
        finally
        {
            preparationLease?.Dispose();
        }
    }

    internal static void PublishExternalStageProgress(
        BMSLibrary.Lr2SynchronizationOwner host,
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

        return result.IncompleteReason ?? result.FinalStage ?? string.Empty;
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
