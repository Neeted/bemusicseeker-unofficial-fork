using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : ILr2SongDbSyncRequestHost
{
    BmsLibraryOptionsSnapshot ILr2SongDbSyncRequestHost.CurrentOptionsSnapshot
    {
        get
        {
            return CurrentOptionsSnapshot;
        }
    }

    bool ILr2SongDbSyncRequestHost.IsShutdownRequested
    {
        get
        {
            return IsShutdownRequested;
        }
    }

    Func<string, string, string, Func<Task>, bool> ILr2SongDbSyncRequestHost.StartupBackgroundTaskScheduler
    {
        get
        {
            return StartupBackgroundTaskScheduler;
        }
    }

    int ILr2SongDbSyncRequestHost.GetLr2SongDbSyncMutationInProgress()
    {
        lock (lockLr2SongDbSync)
        {
            return lr2SongDbSyncMutationInProgress;
        }
    }

    Lr2SongDbSyncRuntimeSnapshot ILr2SongDbSyncRequestHost.GetLr2SongDbSyncRuntimeSnapshot()
    {
        lock (lockLr2SongDbSync)
        {
            return CreateLr2SongDbSyncRuntimeSnapshotUnsafe();
        }
    }

    bool ILr2SongDbSyncRequestHost.TryReserveLr2SongDbSyncPreparation(
        bool requiresPreparation,
        out Lr2SongDbSyncRuntimeSnapshot blockingSnapshot)
    {
        lock (lockLr2SongDbSync)
        {
            if (_Lr2SongDbSyncRunning || lr2SongDbSyncPrepareInProgress || lr2SongDbSyncMutationInProgress > 0)
            {
                blockingSnapshot = CreateLr2SongDbSyncRuntimeSnapshotUnsafe();
                return false;
            }
            if (requiresPreparation)
            {
                lr2SongDbSyncPrepareInProgress = true;
            }
            blockingSnapshot = CreateLr2SongDbSyncRuntimeSnapshotUnsafe();
            return true;
        }
    }

    Lr2SongDbSyncStatusSnapshot ILr2SongDbSyncRequestHost.GetLr2SongDbSyncStatusSnapshot()
    {
        return GetLr2SongDbSyncStatusSnapshot();
    }

    LR2SongDBExtended ILr2SongDbSyncRequestHost.OpenSongDb()
    {
        return dbGateway.OpenSongDb();
    }

    IDisposable ILr2SongDbSyncRequestHost.EnterLr2SongDbSyncCatalogMutationLease()
    {
        return catalogMutationOwner.EnterMaintenanceWriteGuard();
    }

    void ILr2SongDbSyncRequestHost.PublishLr2SongDbSyncStatus(Lr2SongDbSyncStatusSnapshot status)
    {
        PublishLr2SongDbSyncStatus(status);
    }

    bool ILr2SongDbSyncRequestHost.TrySkipForShutdown(string operation, string reason)
    {
        return TrySkipForShutdown(operation, reason);
    }

    void ILr2SongDbSyncRequestHost.ClearLr2SongDbSyncPreparedDataSurface(string reason)
    {
        ClearLr2SongDbSyncPreparedDataSurface(reason);
    }

    void ILr2SongDbSyncRequestHost.ApplyLr2SongDbSyncPreparedDataSurface(
        string reason,
        Lr2SongDbSyncPreparedDataSurface preparedSurface)
    {
        ApplyLr2SongDbSyncPreparedDataSurface(reason, preparedSurface);
    }

    void ILr2SongDbSyncRequestHost.ClearLr2SongDbSyncPrepareReservation()
    {
        ClearLr2SongDbSyncPrepareReservation();
    }

    bool ILr2SongDbSyncRequestHost.TryBeginLr2SongDbSyncRequest(out int requestVersion)
    {
        return TryBeginLr2SongDbSyncRequest(out requestVersion);
    }

    void ILr2SongDbSyncRequestHost.CompleteLr2SongDbSyncRequest(int requestVersion, string stage)
    {
        CompleteLr2SongDbSyncRequest(requestVersion, stage);
    }

    void ILr2SongDbSyncRequestHost.FailLr2SongDbSyncRequest(
        int requestVersion,
        Lr2SongDbSyncStatusKind status,
        string stage,
        string message)
    {
        FailLr2SongDbSyncRequest(requestVersion, status, stage, message);
    }

    void ILr2SongDbSyncRequestHost.RunLr2SongDbSync(string reason, string signature, int requestVersion)
    {
        RunLr2SongDbSync(reason, signature, requestVersion);
    }

    CancellationToken ILr2SongDbSyncRequestHost.GetLr2SongDbSyncCancellationToken()
    {
        lock (lockLr2SongDbSync)
        {
            return lr2SongDbSyncCancellation?.Token ?? CancellationToken.None;
        }
    }

    Lr2SongDbSyncInput ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncInput()
    {
        return CreateLr2SongDbSyncInput();
    }

    TimeSpan ILr2SongDbSyncRequestHost.CurrentChartInfoParseTimeout
    {
        get
        {
            return chartInfoBuildService.CurrentParseTimeout;
        }
    }

    void ILr2SongDbSyncRequestHost.ReportStartupBackgroundTask(
        string name,
        string status,
        long elapsedMs,
        bool failed,
        string detail)
    {
        ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);
    }

    void ILr2SongDbSyncRequestHost.PublishLr2SongDbSyncPreflightStage(string stage, string reason, string runId)
    {
        PublishLr2SongDbSyncPreflightStage(stage, reason, runId);
    }

    void ILr2SongDbSyncRequestHost.LogLr2SongDbSyncPreflightStageDone(string stage, string reason, string runId, long elapsedMs)
    {
        LogLr2SongDbSyncPreflightStageDone(stage, reason, runId, elapsedMs);
    }

    void ILr2SongDbSyncRequestHost.EnsureLr2SongDbSyncChartInfoIndexHydrated(string reason)
    {
        EnsureLr2SongDbSyncChartInfoIndexHydrated(reason);
    }

    Dictionary<string, BMSFile> ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncCompatibilityProjectionIndex()
    {
        return CreateLr2SongDbSyncCompatibilityProjectionIndex();
    }

    Func<BMSFile, LR2SongDBExtended.chart_info> ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncChartInfoResolverSnapshot()
    {
        return CreateLr2SongDbSyncChartInfoResolverSnapshot();
    }

    HashSet<string> ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(string reason)
    {
        return CreateLr2SongDbSyncCurrentChartInfoParseFailureMd5Snapshot(reason);
    }

    void ILr2SongDbSyncRequestHost.UpsertLr2SongDbSyncChartInfoIndexRows(IReadOnlyList<LR2SongDBExtended.chart_info> rows)
    {
        UpsertChartInfoIndexRows(rows, "lr2_song_db_sync_inline_chart_info", dispatchPresentation: false);
    }

    CatalogChartInfoWriteReceipt ILr2SongDbSyncRequestHost.ApplyLr2SongDbSyncChartInfoWrite(
        LR2SongDBExtended songDb,
        CatalogChartInfoWriteRequest request)
    {
        return catalogMutationOwner.ApplyChartInfoWriteInTransaction(songDb, request);
    }

    bool ILr2SongDbSyncRequestHost.IsLr2SongDbSyncInputCurrent(Lr2SongDbSyncInput input)
    {
        return IsLr2SongDbSyncInputCurrent(input);
    }

    void ILr2SongDbSyncRequestHost.UpdateLr2SongDbSyncProgress(Lr2SongDbSyncProgress progress)
    {
        UpdateLr2SongDbSyncProgress(progress);
    }

    int ILr2SongDbSyncRequestHost.ApplyLr2SongDbSyncCompatibilityProjection(
        IReadOnlyList<BMSFileMaintenanceInfo> maintenanceInfos,
        string reason,
        IReadOnlyDictionary<string, BMSFile> bmsByPath,
        bool logSummary,
        bool dispatchPresentation)
    {
        return ApplyLr2SongDbSyncCompatibilityProjection(
            maintenanceInfos,
            reason,
            bmsByPath,
            logSummary,
            dispatchPresentation);
    }

    ISet<string> ILr2SongDbSyncRequestHost.GetLr2SongDbSyncTransientSongRowsSkipPaths(Lr2SongDbSyncInput input, string reason)
    {
        return GetLr2SongDbSyncTransientSongRowsSkipPaths(input, reason);
    }

    Lr2SongDbSyncSongRowsSkipVerificationResult ILr2SongDbSyncRequestHost.VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(
        LR2SongDBExtended songDb,
        IReadOnlyList<BMSFile> songRows,
        Lr2SongDbSyncInput input,
        string reason)
    {
        return VerifyLr2SongDbSyncSongRowsFreshFromFileDiff(songDb, songRows, input, reason);
    }

    void ILr2SongDbSyncRequestHost.DispatchWarningPresentationChanged(string reason)
    {
        DispatchWarningPresentationChanged(reason);
    }

    void ILr2SongDbSyncRequestHost.MarkLr2SongDbSyncPreflightCancelled(string signature, string runId, string stage)
    {
        MarkLr2SongDbSyncPreflightCancelled(signature, runId, stage);
    }

    void ILr2SongDbSyncRequestHost.MarkLr2SongDbSyncFailedStatus(string signature, string runId, Exception ex)
    {
        using LR2SongDBExtended songDb = dbGateway.OpenSongDb();
        Lr2SongDbSyncStatusService.MarkFailed(
            songDb,
            signature,
            runId,
            processedCursor: null,
            totalCount: null,
            stage: "failed",
            error: ex.Message,
            nowUtc: DateTime.UtcNow);
    }

    void ILr2SongDbSyncRequestHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }

    private Lr2SongDbSyncRuntimeSnapshot CreateLr2SongDbSyncRuntimeSnapshotUnsafe()
    {
        return new Lr2SongDbSyncRuntimeSnapshot
        {
            Running = _Lr2SongDbSyncRunning,
            Preparing = lr2SongDbSyncPrepareInProgress,
            MutationInProgress = lr2SongDbSyncMutationInProgress,
            RequestedVersion = Lr2SongDbSyncRequestedVersion,
            Stage = Lr2SongDbSyncStage ?? string.Empty,
            ProcessedCount = Lr2SongDbSyncProcessedCount,
            TotalCount = Lr2SongDbSyncTotalCount,
            StageProcessedCount = Lr2SongDbSyncStageProcessedCount,
            StageTotalCount = Lr2SongDbSyncStageTotalCount
        };
    }
}
