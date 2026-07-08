using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IInstallableMaintenanceDeferredHost
{
    bool IsShutdownRequested { get; }

    Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; }

    bool TrySkipForShutdown(string operation, string reason);

    InstallableMaintenanceQueueState QueueInstallableMaintenanceRequest(long criticalElapsedMs);

    int CompleteInstallableMaintenanceForShutdown();

    InstallableMaintenanceRequestState GetInstallableMaintenanceRequest();

    void MarkInstallableMaintenanceSkipped(int requestVersion);

    bool CompleteInstallableMaintenanceRequest(int requestVersion);

    int CountInstallableMaintenanceSnapshotTargets();

    InstallableMaintenanceSnapshot CreateInstallableMaintenanceSnapshot();

    int SetModeAndCommitToDb(InstallableMaintenanceSnapshot snapshot);

    MaintenanceWorkflowResult SetInstallableMaintenanceInfo();

    void ResetInstallableMaintenanceWriteLockFlags();

    void ReleaseInstallableMaintenanceSnapshot(InstallableMaintenanceSnapshot snapshot);

    string GetDisplayedExceptionMessage(Exception ex);

    void LogInstallPerformance(string message);

    void LogStartupMemoryCheckpoint(string scope, string phase);
}

internal sealed class InstallableMaintenanceQueueState
{
    public int Version { get; set; }

    public bool ShouldStartWorker { get; set; }
}

internal sealed class InstallableMaintenanceRequestState
{
    public int Version { get; set; }

    public long CriticalElapsedMs { get; set; }
}

internal sealed class InstallableMaintenanceSnapshot
{
    public InstallableMaintenanceSnapshot(List<BMSFile> files, int snapshotCount)
    {
        Files = files;
        SnapshotCount = snapshotCount;
    }

    public List<BMSFile> Files { get; }

    public int SnapshotCount { get; }
}

internal static class InstallableMaintenanceDeferredCoordinator
{
    internal static void Queue(IInstallableMaintenanceDeferredHost host, string reason, long criticalElapsedMs, string dependency)
    {
        if (host.TrySkipForShutdown("installable_maintenance", reason))
        {
            return;
        }
        InstallableMaintenanceQueueState queueState = host.QueueInstallableMaintenanceRequest(criticalElapsedMs);
        int queueSnapshotCount = host.CountInstallableMaintenanceSnapshotTargets();
        host.LogInstallPerformance("installable_maintenance_deferred queue reason=" + (reason ?? "unknown")
            + " version=" + queueState.Version
            + " snapshotCount=" + queueSnapshotCount
            + " criticalMs=" + criticalElapsedMs);
        if (!queueState.ShouldStartWorker)
        {
            return;
        }

        Task work()
        {
            ProcessRequests(host);
            return Task.CompletedTask;
        }
        if (host.StartupBackgroundTaskScheduler != null)
        {
            if (host.StartupBackgroundTaskScheduler("installable_maintenance", reason ?? "queue", dependency, work))
            {
                return;
            }
            CompleteForShutdown(host, "startup_scheduler_rejected");
            return;
        }
        if (host.IsShutdownRequested)
        {
            CompleteForShutdown(host, "shutdown_requested");
            return;
        }
        Task.Run(() => ProcessRequests(host)).Logging("ProcessDeferredInstallableMaintenance");
    }

    private static void CompleteForShutdown(IInstallableMaintenanceDeferredHost host, string shutdownReason)
    {
        int requestVersion = host.CompleteInstallableMaintenanceForShutdown();
        host.LogInstallPerformance("installable_maintenance_deferred skipped version=" + requestVersion + " reason=" + (shutdownReason ?? "shutdown_requested"));
    }

    private static void ProcessRequests(IInstallableMaintenanceDeferredHost host)
    {
        while (true)
        {
            InstallableMaintenanceRequestState request = host.GetInstallableMaintenanceRequest();
            if (host.IsShutdownRequested)
            {
                host.MarkInstallableMaintenanceSkipped(request.Version);
                host.LogInstallPerformance("installable_maintenance_deferred skipped version=" + request.Version + " reason=shutdown_requested");
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            long setModeMs = 0L;
            long setHealthMs = 0L;
            const long setZeroNoteMs = 0L;
            int snapshotCount = 0;
            int setModeTargetCount = 0;
            var maintenanceResult = new MaintenanceWorkflowResult();
            InstallableMaintenanceSnapshot snapshot = null;
            try
            {
                snapshot = host.CreateInstallableMaintenanceSnapshot();
                snapshotCount = snapshot.SnapshotCount;
                host.LogInstallPerformance("installable_maintenance_deferred run version=" + request.Version
                    + " snapshotCount=" + snapshotCount
                    + " criticalMs=" + request.CriticalElapsedMs);
                var stopwatchSetMode = Stopwatch.StartNew();
                setModeTargetCount = host.SetModeAndCommitToDb(snapshot);
                stopwatchSetMode.Stop();
                setModeMs = stopwatchSetMode.ElapsedMilliseconds;

                var stopwatchSetHealth = Stopwatch.StartNew();
                maintenanceResult = host.SetInstallableMaintenanceInfo() ?? new MaintenanceWorkflowResult();
                stopwatchSetHealth.Stop();
                setHealthMs = stopwatchSetHealth.ElapsedMilliseconds;
                host.ResetInstallableMaintenanceWriteLockFlags();

                stopwatch.Stop();
                LogCompleted(
                    host,
                    request,
                    maintenanceResult,
                    snapshotCount,
                    setModeTargetCount,
                    setModeMs,
                    setHealthMs,
                    setZeroNoteMs,
                    stopwatch.ElapsedMilliseconds);
                host.LogInstallPerformance("init_library_installable critical_ms=" + request.CriticalElapsedMs + " deferred_ms=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogFailed(
                    host,
                    request,
                    maintenanceResult,
                    snapshotCount,
                    setModeTargetCount,
                    setModeMs,
                    setHealthMs,
                    setZeroNoteMs,
                    stopwatch.ElapsedMilliseconds,
                    ex);
            }
            finally
            {
                host.ResetInstallableMaintenanceWriteLockFlags();
                host.ReleaseInstallableMaintenanceSnapshot(snapshot);
                host.LogStartupMemoryCheckpoint("installable_maintenance_deferred", "after_release");
            }

            if (host.CompleteInstallableMaintenanceRequest(request.Version))
            {
                return;
            }
        }
    }

    private static void LogCompleted(
        IInstallableMaintenanceDeferredHost host,
        InstallableMaintenanceRequestState request,
        MaintenanceWorkflowResult maintenanceResult,
        int snapshotCount,
        int setModeTargetCount,
        long setModeMs,
        long setHealthMs,
        long setZeroNoteMs,
        long elapsedMs)
    {
        host.LogInstallPerformance("installable_maintenance_deferred done version=" + request.Version
            + " criticalMs=" + request.CriticalElapsedMs
            + " snapshotCount=" + snapshotCount
            + " setModeTargets=" + setModeTargetCount
            + " maintenanceChecked=" + maintenanceResult.CheckedFileCount
            + " bmsResourceTargets=" + maintenanceResult.BmsResourceTargetCount
            + " bmsonResourceTargets=" + maintenanceResult.BmsonResourceTargetCount
            + " healthTargetCount=" + maintenanceResult.HealthTargetCount
            + " healthDegree=" + maintenanceResult.HealthDegree
            + " forceTargets=" + maintenanceResult.ForceTargetCount
            + " missingInfoTargets=" + maintenanceResult.MissingInfoTargetCount
            + " missingEncodingTargets=" + maintenanceResult.MissingEncodingTargetCount
            + " bmsonMissingFreshRefs=" + maintenanceResult.BmsonMissingFreshResourceReferenceCount
            + " healthMs=" + maintenanceResult.HealthMs
            + " encodingMs=" + maintenanceResult.EncodingMs
            + " bmsonRefreshMs=" + maintenanceResult.BmsonRefreshMs
            + " healthCacheHit=" + maintenanceResult.HealthCacheHitCount
            + " healthFileExistsFallback=" + maintenanceResult.HealthFileExistsFallbackCount
            + " healthFileExistsFallbackAudio=" + maintenanceResult.HealthAudioFileExistsFallbackCount
            + " healthFileExistsFallbackImage=" + maintenanceResult.HealthImageFileExistsFallbackCount
            + " healthFileExistsFallbackMovie=" + maintenanceResult.HealthMovieFileExistsFallbackCount
            + " healthFileExistsFallbackOptionalImage=" + maintenanceResult.HealthOptionalImageFileExistsFallbackCount
            + " maintenanceUpserted=" + maintenanceResult.MaintenanceInfoUpsertCount
            + " maintenanceUnchanged=" + maintenanceResult.MaintenanceInfoUnchangedCount
            + " bmsonReparsed=" + maintenanceResult.BmsonReparsedCount
            + " bmsonReparseFailed=" + maintenanceResult.BmsonReparseFailedCount
            + " bmsonResourceRefsReused=" + maintenanceResult.BmsonResourceReferenceReusedCount
            + " songReloaded=" + maintenanceResult.ReloadedSongCount
            + " readMs=" + maintenanceResult.ReadMs
            + " computeMs=" + maintenanceResult.ComputeMs
            + " commitMs=" + maintenanceResult.CommitMs
            + " resourceHealthIndexMs=" + maintenanceResult.ResourceHealthIndexMs
            + " warningReapplyTargets=" + maintenanceResult.WarningReapplyTargets
            + " warningChanged=" + maintenanceResult.WarningChangedCount
            + " set_mode_ms=" + setModeMs
            + " set_health_ms=" + setHealthMs
            + " set_zero_note_ms=" + setZeroNoteMs
            + " deferred_ms=" + elapsedMs);
    }

    private static void LogFailed(
        IInstallableMaintenanceDeferredHost host,
        InstallableMaintenanceRequestState request,
        MaintenanceWorkflowResult maintenanceResult,
        int snapshotCount,
        int setModeTargetCount,
        long setModeMs,
        long setHealthMs,
        long setZeroNoteMs,
        long elapsedMs,
        Exception ex)
    {
        host.LogInstallPerformance("installable_maintenance_deferred failed version=" + request.Version
            + " criticalMs=" + request.CriticalElapsedMs
            + " snapshotCount=" + snapshotCount
            + " setModeTargets=" + setModeTargetCount
            + " maintenanceChecked=" + maintenanceResult.CheckedFileCount
            + " bmsResourceTargets=" + maintenanceResult.BmsResourceTargetCount
            + " bmsonResourceTargets=" + maintenanceResult.BmsonResourceTargetCount
            + " healthTargetCount=" + maintenanceResult.HealthTargetCount
            + " healthDegree=" + maintenanceResult.HealthDegree
            + " forceTargets=" + maintenanceResult.ForceTargetCount
            + " missingInfoTargets=" + maintenanceResult.MissingInfoTargetCount
            + " missingEncodingTargets=" + maintenanceResult.MissingEncodingTargetCount
            + " bmsonMissingFreshRefs=" + maintenanceResult.BmsonMissingFreshResourceReferenceCount
            + " healthMs=" + maintenanceResult.HealthMs
            + " encodingMs=" + maintenanceResult.EncodingMs
            + " bmsonRefreshMs=" + maintenanceResult.BmsonRefreshMs
            + " healthCacheHit=" + maintenanceResult.HealthCacheHitCount
            + " healthFileExistsFallback=" + maintenanceResult.HealthFileExistsFallbackCount
            + " healthFileExistsFallbackAudio=" + maintenanceResult.HealthAudioFileExistsFallbackCount
            + " healthFileExistsFallbackImage=" + maintenanceResult.HealthImageFileExistsFallbackCount
            + " healthFileExistsFallbackMovie=" + maintenanceResult.HealthMovieFileExistsFallbackCount
            + " healthFileExistsFallbackOptionalImage=" + maintenanceResult.HealthOptionalImageFileExistsFallbackCount
            + " maintenanceUpserted=" + maintenanceResult.MaintenanceInfoUpsertCount
            + " bmsonReparsed=" + maintenanceResult.BmsonReparsedCount
            + " bmsonReparseFailed=" + maintenanceResult.BmsonReparseFailedCount
            + " bmsonResourceRefsReused=" + maintenanceResult.BmsonResourceReferenceReusedCount
            + " songReloaded=" + maintenanceResult.ReloadedSongCount
            + " resourceHealthIndexMs=" + maintenanceResult.ResourceHealthIndexMs
            + " warningReapplyTargets=" + maintenanceResult.WarningReapplyTargets
            + " warningChanged=" + maintenanceResult.WarningChangedCount
            + " set_mode_ms=" + setModeMs
            + " set_health_ms=" + setHealthMs
            + " set_zero_note_ms=" + setZeroNoteMs
            + " deferred_ms=" + elapsedMs
            + " message=" + host.GetDisplayedExceptionMessage(ex).Replace(Environment.NewLine, " | "));
    }
}
