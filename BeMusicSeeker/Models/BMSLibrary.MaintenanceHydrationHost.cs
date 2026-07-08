using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IMaintenanceHydrationHost
{
    BmsLibraryOptionsSnapshot IMaintenanceHydrationHost.CurrentOptionsSnapshot
    {
        get
        {
            return CurrentOptionsSnapshot;
        }
    }

    bool IMaintenanceHydrationHost.IsShutdownRequested
    {
        get
        {
            return IsShutdownRequested;
        }
    }

    Func<string, string, string, Func<Task>, bool> IMaintenanceHydrationHost.StartupBackgroundTaskScheduler
    {
        get
        {
            return StartupBackgroundTaskScheduler;
        }
    }

    bool IMaintenanceHydrationHost.TrySkipForShutdown(string operation, string reason)
    {
        return TrySkipForShutdown(operation, reason);
    }

    MaintenanceHydrationQueueState IMaintenanceHydrationHost.QueueMaintenanceHydrationRequest()
    {
        int version;
        bool shouldStartWorker = false;
        lock (lockDeferredMaintenanceHydration)
        {
            MaintenanceHydrationRequestedVersion++;
            version = MaintenanceHydrationRequestedVersion;
            if (!MaintenanceHydrationRunning)
            {
                MaintenanceHydrationRunning = true;
                shouldStartWorker = true;
            }
        }

        return new MaintenanceHydrationQueueState
        {
            Version = version,
            ShouldStartWorker = shouldStartWorker
        };
    }

    int IMaintenanceHydrationHost.CompleteMaintenanceHydrationForShutdown()
    {
        int requestVersion;
        lock (lockDeferredMaintenanceHydration)
        {
            requestVersion = MaintenanceHydrationRequestedVersion;
            MaintenanceHydrationRunning = false;
        }
        MaintenanceHydrationCompletedVersion = requestVersion;
        MaintenanceHydrationRunning = false;
        return requestVersion;
    }

    int IMaintenanceHydrationHost.GetMaintenanceHydrationRequestedVersion()
    {
        lock (lockDeferredMaintenanceHydration)
        {
            return MaintenanceHydrationRequestedVersion;
        }
    }

    void IMaintenanceHydrationHost.MarkMaintenanceHydrationSkipped(int requestVersion)
    {
        MaintenanceHydrationCompletedVersion = requestVersion;
        MaintenanceHydrationRunning = false;
    }

    bool IMaintenanceHydrationHost.CompleteMaintenanceHydrationRequest(int requestVersion)
    {
        lock (lockDeferredMaintenanceHydration)
        {
            MaintenanceHydrationCompletedVersion = requestVersion;
            if (requestVersion == MaintenanceHydrationRequestedVersion)
            {
                MaintenanceHydrationRunning = false;
                return true;
            }
        }
        return false;
    }

    MaintenanceTableHydrationResult IMaintenanceHydrationHost.LoadMaintenanceTable(BmsLibraryOptionsSnapshot options)
    {
        return initializationService.LoadMaintenanceTable(
            dbGateway,
            options,
            LogInstallPerformance);
    }

    void IMaintenanceHydrationHost.ApplyMaintenanceHydrationResult(MaintenanceTableHydrationResult result)
    {
        ApplyMaintenanceHydrationResult(result);
    }

    string IMaintenanceHydrationHost.GetDisplayedExceptionMessage(Exception ex)
    {
        return GetDisplayedExceptionMessage(ex);
    }

    void IMaintenanceHydrationHost.ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail)
    {
        ReportStartupBackgroundTask(name, status, elapsedMs, failed, detail);
    }

    void IMaintenanceHydrationHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }
}
