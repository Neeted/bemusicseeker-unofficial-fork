using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IMaintenanceHydrationHost
{
    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    bool IsShutdownRequested { get; }

    Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; }

    bool TrySkipForShutdown(string operation, string reason);

    MaintenanceHydrationQueueState QueueMaintenanceHydrationRequest();

    int CompleteMaintenanceHydrationForShutdown();

    int GetMaintenanceHydrationRequestedVersion();

    void MarkMaintenanceHydrationSkipped(int requestVersion);

    bool CompleteMaintenanceHydrationRequest(int requestVersion);

    MaintenanceTableHydrationResult LoadMaintenanceTable(BmsLibraryOptionsSnapshot options);

    void ApplyMaintenanceHydrationResult(MaintenanceTableHydrationResult result);

    string GetDisplayedExceptionMessage(Exception ex);

    void ReportStartupBackgroundTask(string name, string status, long elapsedMs, bool failed, string detail = null);

    void LogInstallPerformance(string message);
}

internal sealed class MaintenanceHydrationQueueState
{
    public int Version { get; set; }

    public bool ShouldStartWorker { get; set; }
}

internal static class MaintenanceHydrationCoordinator
{
    internal static void Queue(IMaintenanceHydrationHost host, string reason)
    {
        if (host.TrySkipForShutdown("maintenance_hydration", reason))
        {
            return;
        }
        MaintenanceHydrationQueueState queueState = host.QueueMaintenanceHydrationRequest();
        host.LogInstallPerformance("maintenance_hydration queue reason=" + (reason ?? "unknown") + " version=" + queueState.Version);
        if (!queueState.ShouldStartWorker)
        {
            return;
        }

        Action createWorker(bool reportDirect) => delegate
        {
            ProcessRequests(host, reportDirect);
        };
        Task work()
        {
            createWorker(false)();
            return Task.CompletedTask;
        }
        if (host.StartupBackgroundTaskScheduler != null)
        {
            if (host.StartupBackgroundTaskScheduler("maintenance_hydration", reason ?? "queue", null, work))
            {
                return;
            }
            CompleteForShutdown(host, "startup_scheduler_rejected", reportDirect: true);
            return;
        }
        if (host.IsShutdownRequested)
        {
            CompleteForShutdown(host, "shutdown_requested", reportDirect: true);
            return;
        }
        host.ReportStartupBackgroundTask("maintenance_hydration", "queued", 0L, failed: false, detail: reason ?? string.Empty);
        Task.Run(createWorker(true)).Logging("ProcessDeferredMaintenanceHydrationRequests");
    }

    private static void CompleteForShutdown(IMaintenanceHydrationHost host, string shutdownReason, bool reportDirect)
    {
        int requestVersion = host.CompleteMaintenanceHydrationForShutdown();
        host.LogInstallPerformance("maintenance_hydration skipped version=" + requestVersion + " reason=" + (shutdownReason ?? "shutdown_requested"));
        if (reportDirect)
        {
            host.ReportStartupBackgroundTask("maintenance_hydration", "skipped", 0L, failed: false, detail: shutdownReason ?? "shutdown_requested");
        }
    }

    private static void ProcessRequests(IMaintenanceHydrationHost host, bool reportDirect)
    {
        while (true)
        {
            int requestVersion = host.GetMaintenanceHydrationRequestedVersion();
            var stopwatch = Stopwatch.StartNew();
            if (host.IsShutdownRequested)
            {
                host.MarkMaintenanceHydrationSkipped(requestVersion);
                host.LogInstallPerformance("maintenance_hydration skipped version=" + requestVersion + " reason=shutdown_requested");
                if (reportDirect)
                {
                    host.ReportStartupBackgroundTask("maintenance_hydration", "skipped", stopwatch.ElapsedMilliseconds, failed: false, detail: "shutdown_requested");
                }
                return;
            }
            if (reportDirect)
            {
                host.ReportStartupBackgroundTask("maintenance_hydration", "start", 0L, failed: false, detail: "version=" + requestVersion);
            }
            try
            {
                BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
                host.LogInstallPerformance("maintenance_hydration start version=" + requestVersion);
                MaintenanceTableHydrationResult result = host.LoadMaintenanceTable(options);
                host.ApplyMaintenanceHydrationResult(result);
                stopwatch.Stop();
                result.TotalMs = stopwatch.ElapsedMilliseconds;
                host.LogInstallPerformance("maintenance_hydration done version=" + requestVersion
                    + " rows=" + result.MaintenanceTableCount
                    + " keys=" + result.MaintenanceMap.Count
                    + " readOnly=" + result.ReadOnly.ToString().ToLowerInvariant()
                    + " dbLockWaitMs=" + result.DbLockWaitMs
                    + " readMs=" + result.MaintenanceTableLoadMs
                    + " countMs=" + result.MaintenanceCountMs
                    + " materializeMs=" + result.MaintenanceMaterializeMs
                    + " mode=" + (string.IsNullOrWhiteSpace(result.MaintenanceMaterializeMode) ? "unknown" : result.MaintenanceMaterializeMode)
                    + " rawRows=" + result.MaintenanceRawRows
                    + " rawReadMs=" + result.MaintenanceRawReadMs
                    + " rawObjectMs=" + result.MaintenanceRawObjectMs
                    + " mapBuildMs=" + result.MaintenanceMapBuildMs
                    + " applyMs=" + result.MaintenanceApplyMs
                    + " attachMs=" + result.MaintenanceAttachMs
                    + " indexBuildMs=" + result.ResourceHealthIndexMs
                    + " cleanupDeleted=" + result.CleanupDeletedCount
                    + " cleanupMs=" + result.CleanupMs
                    + " ownerPathCount=" + result.OwnerPathCount
                    + " stalePathCount=" + result.StalePathCount
                    + " validSnapshotCount=" + result.ValidSnapshotCount
                    + " placeholderCount=" + result.PlaceholderCount
                    + " viewRefreshQueued=" + result.ViewRefreshQueued.ToString().ToLowerInvariant()
                    + " appliedBms=" + result.AppliedBmsCount
                    + " appliedBmson=" + result.AppliedBmsonCount
                    + " defaultBms=" + result.DefaultBmsCount
                    + " defaultBmson=" + result.DefaultBmsonCount
                    + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
                if (reportDirect)
                {
                    host.ReportStartupBackgroundTask("maintenance_hydration", "done", stopwatch.ElapsedMilliseconds, failed: false, detail: "rows=" + result.MaintenanceTableCount + "_appliedBms=" + result.AppliedBmsCount + "_appliedBmson=" + result.AppliedBmsonCount);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                host.LogInstallPerformance("maintenance_hydration failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + host.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
                if (reportDirect)
                {
                    host.ReportStartupBackgroundTask("maintenance_hydration", "failed", stopwatch.ElapsedMilliseconds, failed: true, detail: ex.Message);
                }
            }

            if (host.CompleteMaintenanceHydrationRequest(requestVersion))
            {
                return;
            }
        }
    }
}
