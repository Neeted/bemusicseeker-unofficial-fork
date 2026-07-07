using System;
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

    void ILr2SongDbSyncRequestHost.RunLr2SongDbSync(string reason, string signature, int requestVersion)
    {
        RunLr2SongDbSync(reason, signature, requestVersion);
    }

    BMSLibrary.Lr2SongDbSyncInput ILr2SongDbSyncRequestHost.CreateLr2SongDbSyncInput()
    {
        return CreateLr2SongDbSyncInput();
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
