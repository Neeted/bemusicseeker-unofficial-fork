using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Ribbit.Util;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    private sealed class InstallableMaintenanceRequestState
    {
        internal int Version { get; init; }

        internal long CriticalElapsedMs { get; init; }
    }

    private sealed class InstallableMaintenanceSnapshot
    {
        internal InstallableMaintenanceSnapshot(List<BMSFile> files, int snapshotCount)
        {
            Files = files;
            SnapshotCount = snapshotCount;
        }

        internal List<BMSFile> Files { get; }

        internal int SnapshotCount { get; }
    }

    private void QueueInstallableMaintenanceWorker(string reason, long criticalElapsedMs, string dependency)
    {
        if (TrySkipForShutdown("installable_maintenance", reason))
        {
            return;
        }

        (int Version, bool ShouldStartWorker) queueState = packageLifecycleOwner.QueueInstallableMaintenanceRequest(criticalElapsedMs);

        LogInstallPerformance("installable_maintenance_deferred queue reason=" + (reason ?? "unknown")
            + " version=" + queueState.Version
            + " snapshotCount=" + CountInstallableMaintenanceSnapshotTargets()
            + " criticalMs=" + criticalElapsedMs);
        if (!queueState.ShouldStartWorker)
        {
            return;
        }

        Task work()
        {
            ProcessInstallableMaintenanceRequests();
            return Task.CompletedTask;
        }
        if (StartupBackgroundTaskScheduler != null)
        {
            if (StartupBackgroundTaskScheduler("installable_maintenance", reason ?? "queue", dependency, work))
            {
                return;
            }
            CompleteInstallableMaintenanceForShutdown("startup_scheduler_rejected");
            return;
        }
        if (IsShutdownRequested)
        {
            CompleteInstallableMaintenanceForShutdown("shutdown_requested");
            return;
        }
        Task.Run(() => ProcessInstallableMaintenanceRequests()).Logging("ProcessDeferredInstallableMaintenance");
    }

    private void CompleteInstallableMaintenanceForShutdown(string shutdownReason)
    {
        int requestVersion = packageLifecycleOwner.CompleteInstallableMaintenanceForShutdown();
        LogInstallPerformance("installable_maintenance_deferred skipped version=" + requestVersion + " reason=" + (shutdownReason ?? "shutdown_requested"));
    }

    private InstallableMaintenanceRequestState GetInstallableMaintenanceRequest()
    {
        (int Version, long CriticalElapsedMs) request = packageLifecycleOwner.GetInstallableMaintenanceRequest();
        return new InstallableMaintenanceRequestState
        {
            Version = request.Version,
            CriticalElapsedMs = request.CriticalElapsedMs
        };
    }

    private void MarkInstallableMaintenanceSkipped(int requestVersion)
    {
        packageLifecycleOwner.MarkInstallableMaintenanceSkipped(requestVersion);
    }

    private bool CompleteInstallableMaintenanceRequest(int requestVersion)
    {
        return packageLifecycleOwner.CompleteInstallableMaintenanceRequest(requestVersion);
    }

    private void ProcessInstallableMaintenanceRequests()
    {
        while (true)
        {
            InstallableMaintenanceRequestState request = GetInstallableMaintenanceRequest();
            if (IsShutdownRequested)
            {
                MarkInstallableMaintenanceSkipped(request.Version);
                LogInstallPerformance("installable_maintenance_deferred skipped version=" + request.Version + " reason=shutdown_requested");
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
                using IDisposable mutationScope = lr2SynchronizationOwner.BeginMutationWhenAvailable(
                    "installable_maintenance_deferred");
                snapshot = CreateInstallableMaintenanceSnapshot();
                snapshotCount = snapshot.SnapshotCount;
                LogInstallPerformance("installable_maintenance_deferred run version=" + request.Version
                    + " snapshotCount=" + snapshotCount
                    + " criticalMs=" + request.CriticalElapsedMs);
                var stopwatchSetMode = Stopwatch.StartNew();
                setModeTargetCount = setModeAndCommitToDB(snapshot.Files);
                stopwatchSetMode.Stop();
                setModeMs = stopwatchSetMode.ElapsedMilliseconds;

                var stopwatchSetHealth = Stopwatch.StartNew();
                maintenanceResult = ApplyInstallableCatalogMaintenance("installable_maintenance_deferred") ?? new MaintenanceWorkflowResult();
                stopwatchSetHealth.Stop();
                setHealthMs = stopwatchSetHealth.ElapsedMilliseconds;
                ResetInstallableMaintenanceWriteLockFlags();

                stopwatch.Stop();
                LogCompletedInstallableMaintenance(request, maintenanceResult, snapshotCount, setModeTargetCount, setModeMs, setHealthMs, setZeroNoteMs, stopwatch.ElapsedMilliseconds);
                LogInstallPerformance("init_library_installable critical_ms=" + request.CriticalElapsedMs + " deferred_ms=" + stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                LogFailedInstallableMaintenance(request, maintenanceResult, snapshotCount, setModeTargetCount, setModeMs, setHealthMs, setZeroNoteMs, stopwatch.ElapsedMilliseconds, ex);
            }
            finally
            {
                ResetInstallableMaintenanceWriteLockFlags();
                snapshot?.Files?.Clear();
                LogStartupMemoryCheckpoint("installable_maintenance_deferred", "after_release");
            }

            if (CompleteInstallableMaintenanceRequest(request.Version))
            {
                return;
            }
        }
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

    private void LogCompletedInstallableMaintenance(
        InstallableMaintenanceRequestState request,
        MaintenanceWorkflowResult maintenanceResult,
        int snapshotCount,
        int setModeTargetCount,
        long setModeMs,
        long setHealthMs,
        long setZeroNoteMs,
        long elapsedMs)
    {
        LogInstallPerformance("installable_maintenance_deferred done version=" + request.Version
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

    private void LogFailedInstallableMaintenance(
        InstallableMaintenanceRequestState request,
        MaintenanceWorkflowResult maintenanceResult,
        int snapshotCount,
        int setModeTargetCount,
        long setModeMs,
        long setHealthMs,
        long setZeroNoteMs,
        long elapsedMs,
        Exception exception)
    {
        LogInstallPerformance("installable_maintenance_deferred failed version=" + request.Version
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
            + " message=" + GetDisplayedExceptionMessage(exception).Replace(Environment.NewLine, " | "));
    }
}
