using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 保守評価とDB保守情報の読込みを所有します。不変の変更事実を
/// <see cref="CatalogMutationOwner"/>へ渡し、永続確定後の共通現在値と健全性の反映を接続します。
/// </summary>
internal sealed class CatalogMaintenanceOwner
{
    /// <summary>既存受付スロットで要求の表示識別と通知先を捕捉します。</summary>
    internal Func<string, long, OperationProgressRequest> ProgressRequestFactory { get; set; }

    /// <summary>受付時の実行通知先を捕捉します。</summary>
    internal Action<OperationProgressRequest, bool> RequestProgressReporter { get; set; }

    private OperationProgressRequest hydrationProgressRequest;
    private Action<OperationProgressRequest, bool> hydrationExecutionReporter;

    /// <summary>直近の保守読込み要求に捕捉した発生元です。</summary>
    internal OperationProgressRequest HydrationProgressRequest { get { lock (hydrationStateLock) { return hydrationProgressRequest; } } }

    private readonly BmsLibraryInitializationService initializationService;

    private readonly BmsLibraryMaintenanceService maintenanceService;

    private readonly CatalogMutationOwner catalogMutationOwner;

    private readonly BmsLibraryDbGateway dbGateway;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private readonly Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider;

    private readonly CatalogOwnedCollectionOwner ownedCollectionOwner;

    private readonly Func<OwnedChartCollectionView> ownerViewProvider;

    private readonly Func<string, ResourceMaintenanceTargetSet> fullTargetProvider;

    private readonly Func<IDisposable> enterStorageRowsWriteGuard;

    private readonly LibraryResourceIndexOwner resourceIndexOwner;

    private readonly IBmsLibraryDialogService dialogService;

    private readonly Func<bool> isShutdownRequested;

    private readonly Func<string, string, bool> trySkipForShutdown;

    private readonly Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider;

    private readonly Action<string, string, long, bool, string> reportStartupBackgroundTask;

    private readonly Func<Exception, string> displayedExceptionMessageProvider;

    private readonly Func<CatalogMaintenanceHydrationReceipt, long> publishHydration;

    private readonly Action<string> logPerformance;

    private readonly Action hydrationStateChanged;

    private readonly ChartFileOperationSynchronizer operationAdmission;

    private readonly object hydrationStateLock = new();

    private int hydrationRequestedVersion;

    private bool hydrationRunning;

    private int hydrationCompletedVersion;

    /// <summary>推定入力の必須背景更新を、構成が共有する論理受付へ接続します。</summary>
    /// <param name="operationAdmission">先行操作の実終端を非同期で待つ共有受付。</param>
    internal CatalogMaintenanceOwner(
        BmsLibraryInitializationService initializationService,
        BmsLibraryMaintenanceService maintenanceService,
        CatalogMutationOwner catalogMutationOwner,
        BmsLibraryDbGateway dbGateway,
        ResourceHealthIndexOwner resourceHealthOwner,
        CatalogOwnedCollectionOwner ownedCollectionOwner,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        Func<OwnedChartCollectionView> ownerViewProvider,
        Func<string, ResourceMaintenanceTargetSet> fullTargetProvider,
        Func<IDisposable> enterStorageRowsWriteGuard,
        LibraryResourceIndexOwner resourceIndexOwner,
        IBmsLibraryDialogService dialogService,
        Func<bool> isShutdownRequested,
        Func<string, string, bool> trySkipForShutdown,
        Func<Func<string, string, string, Func<Task>, bool>> startupBackgroundTaskSchedulerProvider,
        Action<string, string, long, bool, string> reportStartupBackgroundTask,
        Func<Exception, string> displayedExceptionMessageProvider,
        Func<CatalogMaintenanceHydrationReceipt, long> publishHydration,
        Action<string> logPerformance,
        Action hydrationStateChanged,
        ChartFileOperationSynchronizer operationAdmission = null)
    {
        this.initializationService = initializationService ?? throw new ArgumentNullException(nameof(initializationService));
        this.maintenanceService = maintenanceService ?? throw new ArgumentNullException(nameof(maintenanceService));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.dbGateway = dbGateway ?? throw new ArgumentNullException(nameof(dbGateway));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
        this.ownedCollectionOwner = ownedCollectionOwner ?? throw new ArgumentNullException(nameof(ownedCollectionOwner));
        this.optionsSnapshotProvider = optionsSnapshotProvider ?? throw new ArgumentNullException(nameof(optionsSnapshotProvider));
        this.ownerViewProvider = ownerViewProvider ?? throw new ArgumentNullException(nameof(ownerViewProvider));
        this.fullTargetProvider = fullTargetProvider ?? throw new ArgumentNullException(nameof(fullTargetProvider));
        this.enterStorageRowsWriteGuard = enterStorageRowsWriteGuard ?? throw new ArgumentNullException(nameof(enterStorageRowsWriteGuard));
        this.resourceIndexOwner = resourceIndexOwner ?? throw new ArgumentNullException(nameof(resourceIndexOwner));
        this.dialogService = dialogService;
        this.isShutdownRequested = isShutdownRequested ?? throw new ArgumentNullException(nameof(isShutdownRequested));
        this.trySkipForShutdown = trySkipForShutdown ?? throw new ArgumentNullException(nameof(trySkipForShutdown));
        this.startupBackgroundTaskSchedulerProvider = startupBackgroundTaskSchedulerProvider;
        this.reportStartupBackgroundTask = reportStartupBackgroundTask;
        this.displayedExceptionMessageProvider = displayedExceptionMessageProvider ?? (ex => ex?.Message ?? string.Empty);
        this.publishHydration = publishHydration ?? throw new ArgumentNullException(nameof(publishHydration));
        this.logPerformance = logPerformance ?? throw new ArgumentNullException(nameof(logPerformance));
        this.hydrationStateChanged = hydrationStateChanged;
        this.operationAdmission = operationAdmission ?? new ChartFileOperationSynchronizer();
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
        string reason,
        IBmsLibraryDialogService dialogServiceOverride = null,
        Action<string> logPerformanceOverride = null,
        Action<CatalogWriteFailureFact> captureFailureFact = null,
        Action<Action> postCommitEffectsObserver = null)
    {
        List<ChartFile> targetCharts = [.. (targetSet.Charts ?? [])
            .Where(chart => chart != null)];
        if (targetCharts.Count == 0)
        {
            return CatalogMaintenanceOperationReceipt.NotApplied;
        }

        // 導入準備時の未所持値から呼ばれた場合も、DB確定後の同じ現在項目を計算入力にします。
        lock (ownedCollectionOwner.Gate)
        {
            targetCharts = [.. targetCharts.Select(chart =>
                ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart))
                    ?? (chart.Token == null ? chart : null)).Where(chart => chart != null)];
        }
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
        Action postCommitEffects = null;
        bool durableCommitBoundaryReached = false;
        bool basicValuesChanged = false;
        CatalogWriteFailureFact failureFact = null;
        try
        {
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
                        dialogServiceOverride ?? dialogService,
                        new ResourceHealthLookupContext(resourceIndexOwner.CaptureSnapshot().DirectoryLookupCache),
                        logPerformanceOverride ?? logPerformance,
                        progressReporter,
                        cancellationToken,
                        () => durableCommitBoundaryReached = true,
                        effects =>
                        {
                            postCommitEffects = effects;
                        });
                    if (workflowResult?.HasUpdates == true)
                    {
                        basicValuesChanged = ApplyCommittedCurrentValues(workflowResult.ChangedCharts);
                        targetCharts = RefreshCurrentTargets(targetCharts);
                        currentTargetSet = targetSet.WithCharts(targetCharts);
                    }
                }
                catch (Exception ex)
                {
                    if (!durableCommitBoundaryReached)
                    {
                        failureFact = CreateMaintenanceWriteFailureFact(ex, mutationReason);
                    }
                    throw;
                }
                finally
                {
                    inputMutation.Dispose();
                }
                targetInputVersion = inputMutation.TargetInputVersion;
            }
        }
        catch
        {
            if (captureFailureFact == null)
            {
                PublishCatalogWriteFailureFactBestEffort(failureFact);
            }
            else
            {
                captureFailureFact(failureFact);
            }
            throw;
        }

        // Publish property changes and terminal progress after the catalog write
        // guard and the resource-health input mutation have both been released.
        if (postCommitEffectsObserver == null)
        {
            postCommitEffects?.Invoke();
        }
        else
        {
            postCommitEffectsObserver(postCommitEffects);
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
            mutationReason,
            basicValuesChanged);
    }

    /// <summary>警告除外の共通値をDB確定後に同じ所持項目へ適用します。</summary>
    internal CatalogMaintenanceOperationReceipt ApplyWarningIgnore(IEnumerable<ChartFile> charts, bool unset, string reason)
    {
        List<ChartFile> targets;
        lock (ownedCollectionOwner.Gate)
        {
            targets = [.. (charts ?? []).Select(chart => ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart)))
                .Where(chart => chart != null)];
        }
        if (targets.Count == 0)
        {
            return CatalogMaintenanceOperationReceipt.NotApplied;
        }

        using (catalogMutationOwner.EnterMaintenanceWriteGuard())
        {
            ResourceHealthIndexOwner.ResourceHealthInputMutation inputMutation = resourceHealthOwner.BeginInputMutation();
            List<ChartFile> values;
            try
            {
                values = maintenanceService.SetChartResourceWarningsIgnored(targets, unset);
                CatalogMaintenanceWriteReceipt receipt = catalogMutationOwner.ApplyMaintenanceWriteUnderGuard(new(
                    values.Select(chart => chart.ResourceHealthMaintenanceSnapshot), currentValues: values));
                if (values.Count > 0 && !receipt.Applied)
                {
                    throw new InvalidOperationException("Maintenance warning changes were not persisted.");
                }

                ApplyCommittedCurrentValues(values);
            }
            catch { resourceHealthOwner.ForceInvalidate(reason ?? "resource_health_ignore_failed"); throw; }
            finally { inputMutation.Dispose(); }
            var mutation = new ResourceHealthIndexMutation();
            mutation.UpdatedTargets.AddRange(values);
            mutation.DeltaBaseResourceHealthInputVersion = inputMutation.BaseInputVersion;
            mutation.DeltaTargetResourceHealthInputVersion = inputMutation.TargetInputVersion;
            mutation.InvalidateIfDeltaFails = true;
            return new(new MaintenanceWorkflowResult(), mutation, reason ?? "resource_health_ignore");
        }
    }

    /// <summary>文字コードの変更を共通値で準備し、DB成功後に現在値へ反映します。</summary>
    internal MaintenanceEncodingUpdateResult ApplyEncoding(IEnumerable<ChartFile> charts, string encoding)
    {
        MaintenanceEncodingUpdateResult result;
        bool durable = false;
        try
        {
            using (catalogMutationOwner.EnterMaintenanceWriteGuard())
            {
                List<ChartFile> targets;
                lock (ownedCollectionOwner.Gate)
                {
                    targets = [.. (charts ?? []).Select(chart => ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart)))
                        .Where(chart => chart != null)];
                }
                result = maintenanceService.ApplyEncoding(targets, encoding);
                if (result.ChangedCharts.Count == 0)
                {
                    return result;
                }

                CatalogMaintenanceWriteReceipt receipt = catalogMutationOwner.ApplyMaintenanceWriteUnderGuard(new(
                    result.MaintenanceInfosToUpsert, result.SongsToUpsert, currentValues: result.ChangedCharts));
                if (!receipt.Applied)
                {
                    throw new InvalidOperationException("Encoding changes were not persisted.");
                }

                durable = true;
                ApplyCommittedCurrentValues(result.ChangedCharts, advanceBasicVersion: result.SongsToUpsert.Count > 0);
            }
        }
        catch (Exception exception)
        {
            if (!durable)
            {
                resourceHealthOwner.ForceInvalidate("lr2_song_db_encoding_upsert_failed");
                PublishCatalogWriteFailureFactBestEffort(CreateMaintenanceWriteFailureFact(exception, "lr2_song_db_encoding_upsert_failed"));
            }
            throw;
        }
        return result;
    }

    /// <summary>DB確定済みの同じ所持項目へ適用し、基本メタデータの実変更または明示要求があれば集合版を一回進めます。</summary>
    /// <returns>基本メタデータに実変更がある場合はtrue。保守投影だけの変更ではfalseです。</returns>
    private bool ApplyCommittedCurrentValues(IEnumerable<ChartFile> values, bool advanceBasicVersion = false)
    {
        lock (ownedCollectionOwner.Gate)
        {
            bool basicValuesChanged = false;
            foreach (ChartFile value in values ?? [])
            {
                ChartFile current = ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(value));
                if (current?.Token != null && ReferenceEquals(current.Token, value.Token))
                {
                    basicValuesChanged |= !BmsLibraryMaintenanceService.HasSameMetadata(current, value);
                    ownedCollectionOwner.Collection.ApplyCurrentChartValue(value);
                }
            }
            if (basicValuesChanged || advanceBasicVersion)
            {
                ownedCollectionOwner.IncrementVersion();
                ownedCollectionOwner.RebaseHashIndexSnapshot();
            }
            return basicValuesChanged;
        }
    }

    private void PublishCatalogWriteFailureFactBestEffort(CatalogWriteFailureFact failureFact)
    {
        catalogMutationOwner.PublishCatalogWriteFailureFactBestEffort(failureFact);
    }

    private static CatalogWriteFailureFact CreateMaintenanceWriteFailureFact(Exception exception, string reason)
    {
        bool encodingUpsert = string.Equals(reason, "lr2_song_db_encoding_upsert_failed", StringComparison.Ordinal);
        string stage = encodingUpsert
            ? "lr2_song_db_encoding_upsert_failed"
            : "lr2_song_db_maintenance_write_failed";
        return new CatalogWriteFailureFact(
            runId: "song_db_write",
            stage,
            logReason: string.IsNullOrWhiteSpace(reason) ? "maintenance_update" : reason,
            exception);
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
            hydrationProgressRequest = ProgressRequestFactory?.Invoke("maintenance_hydration", version);
            hydrationExecutionReporter = RequestProgressReporter;
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

        Func<Task> work = async () =>
        {
            using IDisposable lease = await operationAdmission.EnterAcceptedBackgroundAsync().ConfigureAwait(false);
            ProcessHydrationRequests(reportDirect: false);
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
        Task.Run(async () =>
        {
            using IDisposable lease = await operationAdmission.EnterAcceptedBackgroundAsync().ConfigureAwait(false);
            ProcessHydrationRequests(reportDirect: true);
        }).ObserveFault("ProcessDeferredMaintenanceHydrationRequests");
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
            OperationProgressRequest progressRequest;
            Action<OperationProgressRequest, bool> progressReporter;
            lock (hydrationStateLock)
            {
                requestVersion = hydrationRequestedVersion;
                progressRequest = hydrationProgressRequest;
                progressReporter = hydrationExecutionReporter;
            }
            var stopwatch = Stopwatch.StartNew();
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
            progressReporter?.Invoke(progressRequest, true);
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
                long resourceHealthIndexMs = publishHydration(receipt);
                result.ResourceHealthIndexMs = resourceHealthIndexMs;
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
            finally { progressReporter?.Invoke(progressRequest, false); }
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

        var applyStopwatch = Stopwatch.StartNew();
        ResourceMaintenanceTargetSet fullTargetSet;
        using (enterStorageRowsWriteGuard())
        {
            OwnedChartCollectionView ownerView = ownerViewProvider();
            var attachStopwatch = Stopwatch.StartNew();
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
                var cleanupStopwatch = Stopwatch.StartNew();
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

    private List<ChartFile> RefreshCurrentTargets(IEnumerable<ChartFile> charts)
    {
        lock (ownedCollectionOwner.Gate)
        {
            return [.. (charts ?? []).Select(chart => ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(chart)))
                .Where(chart => chart != null)];
        }
    }

    private void AttachMaintenanceSnapshots(OwnedChartCollectionView ownerView, MaintenanceTableHydrationResult result)
    {
        if (ownerView == null || result == null)
        {
            return;
        }
        lock (ownedCollectionOwner.Gate)
        {
            foreach (ChartFile captured in ownerView.BmsCharts.Concat(ownerView.BmsonCharts))
            {
                ChartFile current = ownedCollectionOwner.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(captured));
                if (current == null || !ReferenceEquals(current.Token, captured.Token))
                {
                    continue;
                }
                ResourceHealthMaintenanceSnapshot maintenance = current.ResourceHealthMaintenanceSnapshot;
                bool matched = result.MaintenanceMap.TryGetValue(current.Path, out ResourceHealthMaintenanceSnapshot stored)
                    && (string.Equals(stored.Hash, current.Md5, StringComparison.OrdinalIgnoreCase)
                        || (current.Kind == ChartFileKind.Bms && string.Equals(stored.Hash, maintenance?.Hash, StringComparison.OrdinalIgnoreCase)));
                if (matched)
                {
                    if (current.Kind == ChartFileKind.Bmson)
                    {
                        stored = stored with
                        {
                            Path = current.Path,
                            Hash = current.Md5,
                            Encoding = "utf-8",
                            Lr2WarningFlags = null,
                            Lr2ResourceMaxRelativeCp932Bytes = null,
                            Lr2ResourceHasParentTraversal = null
                        };
                        result.AppliedBmsonCount++;
                    }
                    else
                    {
                        result.AppliedBmsCount++;
                    }
                    maintenance = stored with { Origin = MaintenanceInfoOrigin.DbHydrated };
                    result.ValidSnapshotCount++;
                }
                else
                {
                    if (current.Kind == ChartFileKind.Bms)
                    {
                        result.DefaultBmsCount++;
                    }
                    else
                    {
                        result.DefaultBmsonCount++;
                    }
                    if (maintenance?.Origin is MaintenanceInfoOrigin.DbHydrated or MaintenanceInfoOrigin.Calculated)
                    {
                        result.ValidSnapshotCount++;
                    }
                    else
                    {
                        maintenance ??= new ResourceHealthMaintenanceSnapshot
                        {
                            Path = current.Path,
                            Hash = current.Md5,
                            Origin = MaintenanceInfoOrigin.Placeholder,
                            StagefileDefined = string.IsNullOrWhiteSpace(current.Stagefile) ? null : true,
                            BannerDefined = string.IsNullOrWhiteSpace(current.Banner) ? null : true,
                            BackbmpDefined = string.IsNullOrWhiteSpace(current.Backbmp) ? null : true
                        };
                        result.PlaceholderCount++;
                    }
                }
                ownedCollectionOwner.Collection.ApplyCurrentChartValue(ChartFileProjection.WithMaintenance(current, maintenance));
            }
        }
    }

    private static void CaptureOwnerPathAndStaleMaintenancePaths(OwnedChartCollectionView ownerView, MaintenanceTableHydrationResult result)
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
        new(new MaintenanceWorkflowResult(), new ResourceHealthIndexMutation(), "maintenance");

    /// <summary>確定済み保守値と索引更新、適用時に判定した基本変更を通知までの短命な結果へ固定します。</summary>
    /// <param name="workflowResult">DB確定後の保守結果と共通現在値。</param>
    /// <param name="resourceHealthMutation">既存の健全性索引へ渡す対象差分。</param>
    /// <param name="reason">既存通知と診断の理由。</param>
    /// <param name="basicValuesChanged">基本メタデータの実変更により集合版を既に一回進めたか。</param>
    internal CatalogMaintenanceOperationReceipt(
        MaintenanceWorkflowResult workflowResult,
        ResourceHealthIndexMutation resourceHealthMutation,
        string reason,
        bool basicValuesChanged = false)
    {
        WorkflowResult = MaintenanceWorkflowResultFacts.From(workflowResult);
        ResourceHealthMutation = (resourceHealthMutation ?? new ResourceHealthIndexMutation()).ToFacts();
        Reason = reason ?? "maintenance";
        BasicValuesChanged = basicValuesChanged;
    }

    internal MaintenanceWorkflowResultFacts WorkflowResult { get; }

    internal ResourceHealthIndexMutationFacts ResourceHealthMutation { get; }

    internal string Reason { get; }

    /// <summary>DB確定値の適用時に基本メタデータが実際に変わり、集合版を既に一回進めたことを示します。</summary>
    internal bool BasicValuesChanged { get; }

}

internal sealed class CatalogMaintenanceHydrationReceipt
{
    internal static CatalogMaintenanceHydrationReceipt Empty { get; } =
        new(new MaintenanceTableHydrationResult(), new ResourceHealthIndexMutation());

    internal CatalogMaintenanceHydrationReceipt(
        MaintenanceTableHydrationResult result,
        ResourceHealthIndexMutation resourceHealthMutation)
    {
        ViewRefreshQueued = result?.ViewRefreshQueued == true;
        ResourceHealthMutation = (resourceHealthMutation ?? new ResourceHealthIndexMutation()).ToFacts();
    }

    internal bool ViewRefreshQueued { get; }

    internal ResourceHealthIndexMutationFacts ResourceHealthMutation { get; }
}

internal sealed class MaintenanceTableHydrationResult
{
    internal List<string> Pragmas { get; } = [];

    internal Dictionary<string, ResourceHealthMaintenanceSnapshot> MaintenanceMap { get; } = new(StringComparer.OrdinalIgnoreCase);

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
