using System;
using System.Diagnostics;
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

    void PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status);

    bool TrySkipForShutdown(string operation, string reason);

    void ClearLr2SongDbSyncPreparedDataSurface(string reason);

    void ApplyLr2SongDbSyncPreparedDataSurface(string reason, Lr2SongDbSyncPreparedDataSurface preparedSurface);

    void ClearLr2SongDbSyncPrepareReservation();

    bool TryBeginLr2SongDbSyncRequest(out int requestVersion);

    void CompleteLr2SongDbSyncRequest(int requestVersion, string stage);

    void RunLr2SongDbSync(string reason, string signature, int requestVersion);

    BMSLibrary.Lr2SongDbSyncInput CreateLr2SongDbSyncInput();

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

        BMSLibrary.Lr2SongDbSyncInput input = host.CreateLr2SongDbSyncInput();
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
