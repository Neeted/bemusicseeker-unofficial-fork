using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IInstallableMaintenanceDeferredHost
{
    bool IInstallableMaintenanceDeferredHost.IsShutdownRequested
    {
        get
        {
            return IsShutdownRequested;
        }
    }

    Func<string, string, string, Func<Task>, bool> IInstallableMaintenanceDeferredHost.StartupBackgroundTaskScheduler
    {
        get
        {
            return StartupBackgroundTaskScheduler;
        }
    }

    bool IInstallableMaintenanceDeferredHost.TrySkipForShutdown(string operation, string reason)
    {
        return TrySkipForShutdown(operation, reason);
    }

    InstallableMaintenanceQueueState IInstallableMaintenanceDeferredHost.QueueInstallableMaintenanceRequest(long criticalElapsedMs)
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredInstallableMaintenance)
        {
            InstallableMaintenanceDeferredRequestedVersion++;
            version = InstallableMaintenanceDeferredRequestedVersion;
            deferredInstallableMaintenanceCriticalElapsedMs = criticalElapsedMs;
            if (!InstallableMaintenanceDeferredRunning)
            {
                InstallableMaintenanceDeferredRunning = true;
                shouldStartWorker = true;
            }
        }

        return new InstallableMaintenanceQueueState
        {
            Version = version,
            ShouldStartWorker = shouldStartWorker
        };
    }

    int IInstallableMaintenanceDeferredHost.CompleteInstallableMaintenanceForShutdown()
    {
        int requestVersion;
        lock (lockDeferredInstallableMaintenance)
        {
            requestVersion = InstallableMaintenanceDeferredRequestedVersion;
            InstallableMaintenanceDeferredRunning = false;
        }
        InstallableMaintenanceDeferredCompletedVersion = requestVersion;
        InstallableMaintenanceDeferredRunning = false;
        return requestVersion;
    }

    InstallableMaintenanceRequestState IInstallableMaintenanceDeferredHost.GetInstallableMaintenanceRequest()
    {
        lock (lockDeferredInstallableMaintenance)
        {
            return new InstallableMaintenanceRequestState
            {
                Version = deferredInstallableMaintenanceRequestedVersion,
                CriticalElapsedMs = deferredInstallableMaintenanceCriticalElapsedMs
            };
        }
    }

    void IInstallableMaintenanceDeferredHost.MarkInstallableMaintenanceSkipped(int requestVersion)
    {
        InstallableMaintenanceDeferredCompletedVersion = requestVersion;
        InstallableMaintenanceDeferredRunning = false;
    }

    bool IInstallableMaintenanceDeferredHost.CompleteInstallableMaintenanceRequest(int requestVersion)
    {
        lock (lockDeferredInstallableMaintenance)
        {
            InstallableMaintenanceDeferredCompletedVersion = requestVersion;
            if (requestVersion == InstallableMaintenanceDeferredRequestedVersion)
            {
                InstallableMaintenanceDeferredRunning = false;
                return true;
            }
        }
        return false;
    }

    int IInstallableMaintenanceDeferredHost.CountInstallableMaintenanceSnapshotTargets()
    {
        return CountInstallableMaintenanceSnapshotTargets();
    }

    InstallableMaintenanceSnapshot IInstallableMaintenanceDeferredHost.CreateInstallableMaintenanceSnapshot()
    {
        return CreateInstallableMaintenanceSnapshot();
    }

    int IInstallableMaintenanceDeferredHost.SetModeAndCommitToDb(InstallableMaintenanceSnapshot snapshot)
    {
        return setModeAndCommitToDB(snapshot?.Files ?? []);
    }

    MaintenanceWorkflowResult IInstallableMaintenanceDeferredHost.ApplyInstallableMaintenance()
    {
        return ApplyInstallableCatalogMaintenance("installable_maintenance_deferred");
    }

    void IInstallableMaintenanceDeferredHost.ResetInstallableMaintenanceWriteLockFlags()
    {
        ResetInstallableMaintenanceWriteLockFlags();
    }

    void IInstallableMaintenanceDeferredHost.ReleaseInstallableMaintenanceSnapshot(InstallableMaintenanceSnapshot snapshot)
    {
        snapshot?.Files?.Clear();
    }

    string IInstallableMaintenanceDeferredHost.GetDisplayedExceptionMessage(Exception ex)
    {
        return GetDisplayedExceptionMessage(ex);
    }

    void IInstallableMaintenanceDeferredHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }

    void IInstallableMaintenanceDeferredHost.LogStartupMemoryCheckpoint(string scope, string phase)
    {
        LogStartupMemoryCheckpoint(scope, phase);
    }

    private InstallableMaintenanceSnapshot CreateInstallableMaintenanceSnapshot()
    {
        using (rwlockBMSFiles.GetReaderGuard())
        {
            List<BMSFile> filesSnapshot = [.. (BMSFiles ?? []).Where(file => file != null)];
            int snapshotCount = CreateOwnedChartStorageOwnerViewUnsafe().Count;
            return new InstallableMaintenanceSnapshot(filesSnapshot, snapshotCount);
        }
    }

    private void ResetInstallableMaintenanceWriteLockFlags()
    {
        IsWriteLockHeldInitializdBMSFilesHealthStatus = false;
        IsWriteLockHeldInitializeBMSFilesEncodingInfo = false;
        IsWriteLockHeldInitializeBMSFilesZeroNote = false;
    }
}
