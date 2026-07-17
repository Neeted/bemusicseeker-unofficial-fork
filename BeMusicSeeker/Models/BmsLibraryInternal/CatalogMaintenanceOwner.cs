using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns catalog maintenance evaluation and maintenance-table hydration.
/// Database writes are submitted as immutable facts to <see cref="CatalogMutationOwner"/>;
/// resource-health changes are returned as a mutation request for the catalog composition.
/// </summary>
internal sealed class CatalogMaintenanceOwner
{
    private readonly BmsLibraryInitializationService initializationService;

    private readonly BmsLibraryMaintenanceService maintenanceService;

    private readonly CatalogMutationOwner catalogMutationOwner;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private readonly Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider;

    private readonly Func<OwnedChartStorageOwnerView> ownerViewProvider;

    private readonly Func<string, ResourceMaintenanceTargetSet> fullTargetProvider;

    private readonly Func<IDisposable> enterStorageRowsWriteGuard;

    private readonly DirectoryResourceLookupCache resourceLookupCache;

    private readonly IBmsLibraryDialogService dialogService;

    private readonly Func<bool> isShutdownRequested;

    private readonly Func<string, string, bool> trySkipForShutdown;

    private readonly Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider;

    private readonly Action<string, string, long, bool, string> reportStartupBackgroundTask;

    private readonly Func<Exception, string> displayedExceptionMessageProvider;

    private readonly Action<CatalogMaintenanceHydrationReceipt> publishHydration;

    private readonly Action<string> logPerformance;

    private readonly Action<Exception, string> markMaintenanceWriteFailure;

    private readonly Action hydrationStateChanged;

    private readonly object hydrationStateLock = new();

    private int hydrationRequestedVersion;

    private bool hydrationRunning;

    private int hydrationCompletedVersion;

    internal CatalogMaintenanceOwner(
        BmsLibraryInitializationService initializationService,
        BmsLibraryMaintenanceService maintenanceService,
        CatalogMutationOwner catalogMutationOwner,
        BmsLibraryDbGateway dbGateway,
        ResourceHealthIndexOwner resourceHealthOwner,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        Func<OwnedChartStorageOwnerView> ownerViewProvider,
        Func<string, ResourceMaintenanceTargetSet> fullTargetProvider,
        Func<IDisposable> enterStorageRowsWriteGuard,
        DirectoryResourceLookupCache resourceLookupCache,
        IBmsLibraryDialogService dialogService,
        Func<bool> isShutdownRequested,
        Func<string, string, bool> trySkipForShutdown,
        Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider,
        Action<string, string, long, bool, string> reportStartupBackgroundTask,
        Func<Exception, string> displayedExceptionMessageProvider,
        Action<CatalogMaintenanceHydrationReceipt> publishHydration,
        Action<string> logPerformance,
        Action<Exception, string> markMaintenanceWriteFailure,
        Action hydrationStateChanged)
    {
        this.initializationService = initializationService ?? throw new ArgumentNullException(nameof(initializationService));
        this.maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
        this.optionsSnapshotProvider = optionsSnapshotProvider ?? throw new ArgumentNullException(nameof(optionsSnapshotProvider));
        this.ownerViewProvider = ownerViewProvider ?? throw new ArgumentNullException(nameof(ownerViewProvider));
        this.fullTargetProvider = fullTargetProvider ?? throw new ArgumentNullException(nameof(fullTargetProvider));
        this.enterStorageRowsWriteGuard = enterStorageRowsWriteGuard ?? throw new ArgumentNullException(nameof(enterStorageRowsWriteGuard));
        this.resourceLookupCache = resourceLookupCache;
        this.dialogService = dialogService;
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.trySkipForShutdown = trySkipForShutdown ?? throw new ArgumentNullException(nameof(trySkipForShutdown));
        this.startupBackgroundTaskSchedulerProvider = startupBackgroundTaskSchedulerProvider;
        this.reportStartupBackgroundTask = reportStartupBackgroundTask;
        this.displayedExceptionMessageProvider = displayedExceptionMessageProvider ?? (ex => ex?.Message ?? string.Empty);
        this.publishHydration = publishHydration ?? throw new ArgumentNullException(nameof(publishHydration));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
        this.markMaintenanceWriteFailure = markMaintenanceWriteFailure;
        this.hydrationStateChanged = hydrationStateChanged;
    }

    internal bool HydrationRunning
    {
        get
        {
            lock (hydrationStateLock)
            {
                return hydrationRunning;
            }
        }
    }

    internal int HydrationRequestedVersion
    {
        get
        {
            lock (hydrationStateLock)
            {
                return hydrationRequestedVersion;
            }
        }
    }

    internal int HydrationCompletedVersion
    {
        get
        {
            lock (hydrationStateLock)
            {
                return hydrationCompletedVersion;
            }
        }
    }

    internal bool HydrationReadyForInstallableMaintenance
    {
        get
        {
            lock (hydrationStateLock)
            {
                return hydrationRequestedVersion > 0
                    && hydrationCompletedVersion >= hydrationRequestedVersion
                    && !hydrationRunning;
            }
        }
    }

    internal CatalogMaintenanceOperationReceipt ApplyMaintenance(
        ResourceMaintenanceTargetSet targetSet,
        bool forceUpdate,
        Action<MaintenanceWorkflowProgress> progressReporter,
        CancellationToken cancellationToken,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        string reason)
    {
        List<ChartFile> targetCharts = [.. (targetSet.Charts ?? [])
            .Where(chart => chart != null)];
        if (targetCharts.Count == 0)
        {
            return CatalogMaintenanceOperationReceipt.NotApplied;
        }

        string mutationReason = string.IsNullOrWhiteSpace(reason) ? "maintenance" : reason;
        MaintenanceWorkflowResult workflowResult;
        int baseInputVersion;
        int targetInputVersion;
        bool indexCurrentBeforeUpdate;
        ResourceMaintenanceTargetSet currentTargetSet = targetSet;
        using (catalogMutationOwner.EnterMaintenanceWriteGuard())
        {
            ResourceHealthIndexOwner.ResourceHealthInputMutation inputMutation = resourceHealthOwner.BeginInputMutation();
            baseInputVersion = inputMutation.BaseInputVersion;
            indexCurrentBeforeUpdate = inputMutation.BaseIndexCurrent;
            try
            {
                workflowResult = maintenanceService.UpdateMaintenanceInfo(
                    targetCharts,
                    forceUpdate,
                    catalogMutationOwner.ApplyMaintenanceWriteUnderGuard,
                    dialogService,
                    new ResourceHealthLookupContext(resourceLookupCache),
                    logPerformance,
                    progressReporter,
                    cancellationToken);
                if (workflowResult?.HasUpdates == true)
                {
                    targetCharts = RefreshTargetsFromCurrentStorageOwners(targetCharts);
                    currentTargetSet = targetSet.WithCharts(targetCharts);
                }
            }
            catch (Exception ex)
            {
                markMaintenanceWriteFailure?.Invoke(ex, mutationReason);
                throw;
            }
            finally
            {
                inputMutation.Dispose();
            }
            targetInputVersion = inputMutation.TargetInputVersion;
        }

        ResourceHealthIndexMutation mutation = ResourceHealthIndexMutationPlanner.BuildMaintenanceMutation(
            currentTargetSet.WithResourceHealthInputVersion(targetInputVersion),
            resourceHealthIndexUpdateMode,
            indexCurrentBeforeUpdate,
            workflowResult?.HasUpdates == true,
            baseInputVersion,
            targetInputVersion);
        return new CatalogMaintenanceOperationReceipt(
            workflowResult ?? new MaintenanceWorkflowResult(),
            mutation,
            targetCharts,
            mutationReason);
    }

    internal CatalogMaintenanceOperationReceipt ApplyWarningIgnore(
        IEnumerable<ChartFile> charts,
        bool unset,
        string reason)
    {
        List<ChartFile> targets = [.. (charts ?? []).Where(chart => chart != null)];
        if (targets.Count == 0)
        {
            return CatalogMaintenanceOperationReceipt.NotApplied;
        }

        Dictionary<BMSFileMaintenanceInfo, bool> previousWarningFlags = [];
        Dictionary<LR2SongDBExtended.bmson_song, BMSFileMaintenanceInfo> previousBmsonMaintenance = [];
        foreach (ChartFile target in targets)
        {
            BMSFile bmsFile = target.GetBmsStorageOwner();
            BMSFileMaintenanceInfo bmsInfo = bmsFile?.TryGetMaintenanceInfoWithoutCreating();
            if (bmsInfo != null)
            {
                previousWarningFlags[bmsInfo] = bmsInfo.is_files_warning_ignored;
            }
            LR2SongDBExtended.bmson_song bmsonSong = target.GetBmsonStorageOwner();
            if (bmsonSong != null)
            {
                previousBmsonMaintenance[bmsonSong] = bmsonSong.MaintenanceInfo;
                if (bmsonSong.MaintenanceInfo != null)
                {
                    previousWarningFlags[bmsonSong.MaintenanceInfo] = bmsonSong.MaintenanceInfo.is_files_warning_ignored;
                }
            }
        }

        using (catalogMutationOwner.EnterMaintenanceWriteGuard())
        {
            ResourceHealthIndexOwner.ResourceHealthInputMutation inputMutation = resourceHealthOwner.BeginInputMutation();
            try
            {
                List<BMSFileMaintenanceInfo> changes = maintenanceService.SetChartResourceWarningsIgnored(targets, unset);
                CatalogMaintenanceWriteReceipt writeReceipt = catalogMutationOwner.ApplyMaintenanceWriteUnderGuard(new CatalogMaintenanceWriteRequest(changes));
                if (changes.Count > 0 && !writeReceipt.Applied)
                {
                    throw new InvalidOperationException("Maintenance warning changes were not persisted.");
                }
            }
            catch
            {
                foreach (KeyValuePair<BMSFileMaintenanceInfo, bool> previous in previousWarningFlags)
                {
                    previous.Key.is_files_warning_ignored = previous.Value;
                }
                foreach (KeyValuePair<LR2SongDBExtended.bmson_song, BMSFileMaintenanceInfo> previous in previousBmsonMaintenance)
                {
                    previous.Key.MaintenanceInfo = previous.Value;
                }
                resourceHealthOwner.ForceInvalidate(reason ?? "resource_health_ignore_failed");
                throw;
            }
            finally
            {
                inputMutation.Dispose();
            }

            var mutation = new ResourceHealthIndexMutation();
            mutation.UpdatedTargets.AddRange(targets);
            mutation.DeltaBaseResourceHealthInputVersion = inputMutation.BaseInputVersion;
            mutation.DeltaTargetResourceHealthInputVersion = inputMutation.TargetInputVersion;
            mutation.InvalidateIfDeltaFails = true;
            return new CatalogMaintenanceOperationReceipt(
                new MaintenanceWorkflowResult(),
                mutation,
                targets,
                reason ?? "resource_health_ignore");
        }
    }

    internal void ApplyEncoding(IEnumerable<BMSFile> bmsFiles, string encoding)
    {
        using (catalogMutationOwner.EnterMaintenanceWriteGuard())
        {
            MaintenanceEncodingUpdateResult updateResult = maintenanceService.ApplyEncoding(bmsFiles, encoding);
            if (updateResult.SongsToUpsert.Count == 0 && updateResult.MaintenanceInfosToUpsert.Count == 0)
            {
                return;
            }
            try
            {
                CatalogMaintenanceWriteReceipt writeReceipt = catalogMutationOwner.ApplyMaintenanceWriteUnderGuard(new CatalogMaintenanceWriteRequest(
                    updateResult.MaintenanceInfosToUpsert,
                    updateResult.SongsToUpsert));
                if ((updateResult.SongsToUpsert.Count > 0 || updateResult.MaintenanceInfosToUpsert.Count > 0)
                    && !writeReceipt.Applied)
                {
                    throw new InvalidOperationException("Encoding changes were not persisted.");
                }
            }
            catch (Exception ex)
            {
                resourceHealthOwner.ForceInvalidate("lr2_song_db_encoding_upsert_failed");
                markMaintenanceWriteFailure?.Invoke(ex, "lr2_song_db_encoding_upsert_failed");
                throw;
            }
        }
    }

    internal void QueueHydration(string reason)
    {
        if (trySkipForShutdown("maintenance_hydration", reason))
        {
            return;
        }

        int version;
        bool shouldStartWorker = false;
        lock (hydrationStateLock)
        {
            hydrationRequestedVersion++;
            version = hydrationRequestedVersion;
            if (!hydrationRunning)
            {
                hydrationRunning = true;
                shouldStartWorker = true;
            }
        }
        hydrationStateChanged?.Invoke();
        logPerformance("maintenance_hydration queue reason=" + (reason ?? "unknown") + " version=" + version);
        if (!shouldStartWorker)
        {
            return;
        }

        Func<Task> work = () =>
        {
            ProcessHydrationRequests(reportDirect: false);
            return Task.CompletedTask;
        };
        Func<string, string, string, Func<Task>, bool> startupBackgroundTaskScheduler = startupBackgroundTaskSchedulerProvider?.Invoke();
        if (startupBackgroundTaskScheduler != null)
        {
            if (startupBackgroundTaskScheduler("maintenance_hydration", reason ?? "queue", null, work))
            {
                return;
            }
            CompleteForShutdown("startup_scheduler_rejected", reportDirect: true);
            return;
        }
        if (isShutdownRequested())
        {
            CompleteForShutdown("shutdown_requested", reportDirect: true);
            return;
        }
        reportStartupBackgroundTask?.Invoke("maintenance_hydration", "queued", 0L, false, reason ?? string.Empty);
        Task.Run(() => ProcessHydrationRequests(reportDirect: true)).Logging("ProcessDeferredMaintenanceHydrationRequests");
    }

    internal void CompleteHydrationForShutdown()
    {
        CompleteForShutdown("shutdown_requested", reportDirect: false);
    }

    private void CompleteForShutdown(string reason, bool reportDirect)
    {
        int requestVersion;
        lock (hydrationStateLock)
        {
            requestVersion = hydrationRequestedVersion;
            hydrationCompletedVersion = requestVersion;
            hydrationRunning = false;
        }
        hydrationStateChanged?.Invoke();
        logPerformance("maintenance_hydration skipped version=" + requestVersion + " reason=" + (reason ?? "shutdown_requested"));
        if (reportDirect)
        {
            reportStartupBackgroundTask?.Invoke("maintenance_hydration", "skipped", 0L, false, reason ?? "shutdown_requested");
        }
    }

    private void ProcessHydrationRequests(bool reportDirect)
    {
        while (true)
        {
            int requestVersion;
            lock (hydrationStateLock)
            {
                requestVersion = hydrationRequestedVersion;
            }
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (isShutdownRequested())
            {
                CompleteHydrationRequest(requestVersion, skipped: true);
                logPerformance("maintenance_hydration skipped version=" + requestVersion + " reason=shutdown_requested");
                if (reportDirect)
                {
                    reportStartupBackgroundTask?.Invoke("maintenance_hydration", "skipped", stopwatch.ElapsedMilliseconds, false, "shutdown_requested");
                }
                return;
            }
            if (reportDirect)
            {
                reportStartupBackgroundTask?.Invoke("maintenance_hydration", "start", 0L, false, "version=" + requestVersion);
            }
            try
            {
                BmsLibraryOptionsSnapshot options = optionsSnapshotProvider();
                logPerformance("maintenance_hydration start version=" + requestVersion);
                MaintenanceTableHydrationResult result = initializationService.LoadMaintenanceTable(
                    dbGateway,
                    options,
                    logPerformance);
                CatalogMaintenanceHydrationReceipt receipt = ApplyHydration(result);
                stopwatch.Stop();
                result.TotalMs = stopwatch.ElapsedMilliseconds;
                publishHydration(receipt);
                LogHydrationCompleted(requestVersion, result, stopwatch.ElapsedMilliseconds);
                if (reportDirect)
                {
                    reportStartupBackgroundTask?.Invoke("maintenance_hydration", "done", stopwatch.ElapsedMilliseconds, false, "rows=" + result.MaintenanceTableCount + "_appliedBms=" + result.AppliedBmsCount + "_appliedBmson=" + result.AppliedBmsonCount);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                logPerformance("maintenance_hydration failed version=" + requestVersion + " elapsedMs=" + stopwatch.ElapsedMilliseconds + " message=" + displayedExceptionMessageProvider(ex).Replace(Environment.NewLine, " | "));
                if (reportDirect)
                {
                    reportStartupBackgroundTask?.Invoke("maintenance_hydration", "failed", stopwatch.ElapsedMilliseconds, true, ex.Message);
                }
            }
            if (CompleteHydrationRequest(requestVersion, skipped: false))
            {
                return;
            }
        }
    }

    private CatalogMaintenanceHydrationReceipt ApplyHydration(MaintenanceTableHydrationResult result)
    {
        if (result == null)
        {
            return CatalogMaintenanceHydrationReceipt.Empty;
        }

        Stopwatch applyStopwatch = Stopwatch.StartNew();
        ResourceMaintenanceTargetSet fullTargetSet;
        using (enterStorageRowsWriteGuard())
        {
            OwnedChartStorageOwnerView ownerView = ownerViewProvider();
            Stopwatch attachStopwatch = Stopwatch.StartNew();
            using (resourceHealthOwner.BeginInputMutation())
            {
                AttachMaintenanceSnapshots(ownerView, result);
            }
            attachStopwatch.Stop();
            result.MaintenanceAttachMs = attachStopwatch.ElapsedMilliseconds;
            fullTargetSet = fullTargetProvider("maintenance_hydration");
            CaptureOwnerPathAndStaleMaintenancePaths(ownerView, result);
            applyStopwatch.Stop();
            result.MaintenanceApplyMs = applyStopwatch.ElapsedMilliseconds;
            if (result.StaleMaintenancePaths.Count > 0)
            {
                Stopwatch cleanupStopwatch = Stopwatch.StartNew();
                try
                {
                    using (catalogMutationOwner.EnterMaintenanceWriteGuard())
                    {
                        CatalogMaintenanceWriteReceipt cleanupReceipt = catalogMutationOwner.ApplyMaintenanceWriteUnderGuard(
                            new CatalogMaintenanceWriteRequest(staleMaintenancePaths: result.StaleMaintenancePaths));
                        result.CleanupDeletedCount = cleanupReceipt.DeletedMaintenanceCount;
                    }
                }
                catch
                {
                    resourceHealthOwner.ForceInvalidate("maintenance_hydration_cleanup_failed");
                    throw;
                }
                finally
                {
                    cleanupStopwatch.Stop();
                    result.CleanupMs = cleanupStopwatch.ElapsedMilliseconds;
                }
            }
        }
        result.ViewRefreshQueued = true;
        return new CatalogMaintenanceHydrationReceipt(
            result,
            ResourceHealthIndexMutationPlanner.BuildMaintenanceHydrationFullRebuildMutation(fullTargetSet));
    }

    private bool CompleteHydrationRequest(int requestVersion, bool skipped)
    {
        bool completedLatestRequest = false;
        lock (hydrationStateLock)
        {
            hydrationCompletedVersion = requestVersion;
            if (requestVersion == hydrationRequestedVersion)
            {
                hydrationRunning = false;
                completedLatestRequest = true;
            }
        }
        hydrationStateChanged?.Invoke();
        return completedLatestRequest;
    }

    private void LogHydrationCompleted(int version, MaintenanceTableHydrationResult result, long elapsedMs)
    {
        logPerformance("maintenance_hydration done version=" + version
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
            + " elapsedMs=" + elapsedMs);
    }

    private static List<ChartFile> RefreshTargetsFromCurrentStorageOwners(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? [])
            .Select(chart => ChartFileProjection.FromStorageOwner(
                chart,
                includeWarningSnapshot: false,
                includeResourceReferences: true,
                includeScoreSnapshot: false))
            .Where(chart => chart != null)];
    }

    private static void AttachMaintenanceSnapshots(OwnedChartStorageOwnerView ownerView, MaintenanceTableHydrationResult result)
    {
        if (ownerView == null || result == null)
        {
            return;
        }
        foreach (BMSFile item in ownerView.BmsFiles)
        {
            if (item == null)
            {
                continue;
            }
            BMSFileMaintenanceInfo nextInfo = null;
            if (!string.IsNullOrWhiteSpace(item.path)
                && result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                && (item.HasMaintenanceInfoHash(value.hash) || string.Equals(value.hash, item.hash, StringComparison.OrdinalIgnoreCase)))
            {
                nextInfo = value;
                result.AppliedBmsCount++;
                item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.DbHydrated);
                result.ValidSnapshotCount++;
            }
            else
            {
                result.DefaultBmsCount++;
                if (item.HasValidMaintenanceInfoSnapshot)
                {
                    result.ValidSnapshotCount++;
                }
                else
                {
                    nextInfo = item.TryGetMaintenanceInfoWithoutCreating() ?? new BMSFileMaintenanceInfo(item);
                    item.SetMaintenanceInfo(nextInfo, suppressPropertyChanged: true, MaintenanceInfoOrigin.Placeholder);
                    result.PlaceholderCount++;
                }
            }
        }
        foreach (LR2SongDBExtended.bmson_song item in ownerView.BmsonSongs)
        {
            if (item == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(item.path)
                && result.MaintenanceMap.TryGetValue(item.path, out BMSFileMaintenanceInfo value)
                && string.Equals(value.hash, item.md5, StringComparison.OrdinalIgnoreCase))
            {
                value.NormalizeForBmson(item.path, item.md5);
                item.MaintenanceInfo = value;
                result.AppliedBmsonCount++;
                result.ValidSnapshotCount++;
            }
            else
            {
                result.DefaultBmsonCount++;
                if (item.MaintenanceInfo != null)
                {
                    result.ValidSnapshotCount++;
                }
                else
                {
                    result.PlaceholderCount++;
                }
            }
        }
    }

    private static void CaptureOwnerPathAndStaleMaintenancePaths(OwnedChartStorageOwnerView ownerView, MaintenanceTableHydrationResult result)
    {
        if (ownerView == null || result == null)
        {
            return;
        }
        result.OwnerPathCount = ownerView.OwnerPathCount;
        foreach (string maintenancePath in result.MaintenanceMap.Keys)
        {
            if (!ownerView.ContainsOwnerPath(maintenancePath))
            {
                result.StaleMaintenancePaths.Add(maintenancePath);
            }
        }
        result.StalePathCount = result.StaleMaintenancePaths.Count;
    }
}

internal sealed class CatalogMaintenanceOperationReceipt
{
    internal static CatalogMaintenanceOperationReceipt NotApplied { get; } =
        new(new MaintenanceWorkflowResult(), new ResourceHealthIndexMutation(), [], "maintenance");

    internal CatalogMaintenanceOperationReceipt(
        MaintenanceWorkflowResult workflowResult,
        ResourceHealthIndexMutation resourceHealthMutation,
        IEnumerable<ChartFile> targetCharts,
        string reason)
    {
        WorkflowResult = workflowResult ?? new MaintenanceWorkflowResult();
        ResourceHealthMutation = resourceHealthMutation ?? new ResourceHealthIndexMutation();
        TargetCharts = Array.AsReadOnly([.. (targetCharts ?? []).Where(chart => chart != null)]);
        Reason = reason ?? "maintenance";
    }

    internal MaintenanceWorkflowResult WorkflowResult { get; }

    internal ResourceHealthIndexMutation ResourceHealthMutation { get; }

    internal IReadOnlyList<ChartFile> TargetCharts { get; }

    internal string Reason { get; }
}

internal sealed class CatalogMaintenanceHydrationReceipt
{
    internal static CatalogMaintenanceHydrationReceipt Empty { get; } =
        new(new MaintenanceTableHydrationResult(), new ResourceHealthIndexMutation());

    internal CatalogMaintenanceHydrationReceipt(
        MaintenanceTableHydrationResult result,
        ResourceHealthIndexMutation resourceHealthMutation)
    {
        Result = result ?? new MaintenanceTableHydrationResult();
        ResourceHealthMutation = resourceHealthMutation ?? new ResourceHealthIndexMutation();
    }

    internal MaintenanceTableHydrationResult Result { get; }

    internal ResourceHealthIndexMutation ResourceHealthMutation { get; }
}

internal sealed class MaintenanceTableHydrationResult
{
    internal List<string> Pragmas { get; } = [];

    internal Dictionary<string, BMSFileMaintenanceInfo> MaintenanceMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal long MaintenanceTableCount { get; set; }
    internal long MaintenanceTableLoadMs { get; set; }
    internal long MaintenanceCountMs { get; set; }
    internal long MaintenanceMaterializeMs { get; set; }
    internal long MaintenanceMapBuildMs { get; set; }
    internal string MaintenanceMaterializeMode { get; set; }
    internal int MaintenanceRawRows { get; set; }
    internal long MaintenanceRawReadMs { get; set; }
    internal long MaintenanceRawObjectMs { get; set; }
    internal bool ReadOnly { get; set; }
    internal long DbLockWaitMs { get; set; }
    internal long MaintenanceApplyMs { get; set; }
    internal long MaintenanceAttachMs { get; set; }
    internal long ResourceHealthIndexMs { get; set; }
    internal long CleanupMs { get; set; }
    internal int AppliedBmsCount { get; set; }
    internal int AppliedBmsonCount { get; set; }
    internal int DefaultBmsCount { get; set; }
    internal int DefaultBmsonCount { get; set; }
    internal int ValidSnapshotCount { get; set; }
    internal int PlaceholderCount { get; set; }
    internal bool ViewRefreshQueued { get; set; }
    internal int OwnerPathCount { get; set; }
    internal int StalePathCount { get; set; }
    internal int CleanupDeletedCount { get; set; }
    internal List<string> StaleMaintenancePaths { get; } = [];
    internal long TotalMs { get; set; }
}
