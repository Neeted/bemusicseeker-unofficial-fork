using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPendingEstimatedInstallPreparationPort
{
    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    IReadOnlyCollection<ChartPackage> PendingPackageSnapshot { get; }

    IDisposable EnterSnapshotLease();

    IDisposable EnterApplyLease();

    IPrimaryHashLookup CreateInstalledChartKeySnapshotForEstimatedInstall(
        EstimatedInstallDeferredFeedback deferredFeedback);

    int CountComponentMoveTargetsForPackage(ChartPackage package, string destinationDirectory, ISet<string> excludedComponentPaths);

    List<ChartPackage> InstallChartPackagesForEstimatedInstall(
        IEnumerable<ChartPackage> installPackages,
        string destinationDirectory,
        List<ChartFile> deferredMaintenanceCharts,
        List<ChartPackage> deferredInstalledPackages,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage,
        IPrimaryHashLookup existingHashes,
        bool skipInstalledPackageWhenNoBms,
        bool deleteSourceContentsAfterSuccessfulInstall,
        EstimatedInstallBatchApplyContext batchApplyContext,
        EstimatedInstallDeferredFeedback deferredFeedback);

    ChartPackage CreateInstalledDisplayPackageForResourceOnlyMerge(ChartPackage originalPackage, string destinationDirectory);

    (bool Success, CleanupSourceKind SourceKind) TryCleanupPendingPackageSourceForEstimatedInstall(
        ChartPackage package,
        EstimatedInstallDeferredFeedback deferredFeedback);
}

/// <summary>
/// Composition boundary for the pending estimated-install mutation gate.
/// The workflow receives a purpose-limited capability instead of the library facade.
/// </summary>
internal sealed class PendingEstimatedInstallMutationGate
{
    private readonly Func<IDisposable>[] guardFactories;

    internal PendingEstimatedInstallMutationGate(params Func<IDisposable>[] guardFactories)
    {
        this.guardFactories = guardFactories ?? [];
    }

    internal IDisposable Enter()
    {
        return PendingEstimatedInstallMutationLease.Acquire(guardFactories);
    }
}

internal interface IPendingEstimatedInstallCatalogPort
{
    PendingEstimatedInstallCatalogPreparation PrepareEstimatedInstallBatchLibraryState(
        EstimatedInstallBatchApplyContext context,
        EstimatedInstallDeferredFeedback deferredFeedback);

    PendingEstimatedInstallCatalogApplyReceipt ApplyEstimatedInstallBatchLibraryState(
        EstimatedInstallBatchApplyContext context,
        PendingEstimatedInstallCatalogPreparation preparation,
        EstimatedInstallDeferredFeedback deferredFeedback);

    void CompleteEstimatedInstallBatchLibraryStateUnderGuard(PendingEstimatedInstallCatalogApplyReceipt receipt);

    void PublishEstimatedInstallBatchLibraryState(PendingEstimatedInstallCatalogApplyReceipt receipt);
}

internal sealed class PendingEstimatedInstallCatalogPreparation
{
    internal IReadOnlyList<string> AffectedDirectories { get; init; } = [];

    internal ChartScanResult DirectoryScan { get; init; }
}

internal sealed class PendingEstimatedInstallCatalogApplyReceipt(
    bool hasFailure,
    Action completeUnderGuard,
    Action publishAfterGuard)
{
    private Action completeUnderGuard =
        completeUnderGuard ?? throw new ArgumentNullException(nameof(completeUnderGuard));

    private Action publishAfterGuard =
        publishAfterGuard ?? throw new ArgumentNullException(nameof(publishAfterGuard));

    internal bool HasFailure { get; } = hasFailure;

    internal void CompleteUnderGuard()
    {
        Interlocked.Exchange(ref completeUnderGuard, null)?.Invoke();
    }

    internal void PublishAfterGuard()
    {
        Interlocked.Exchange(ref publishAfterGuard, null)?.Invoke();
    }
}

internal interface IPendingEstimatedInstallMaintenancePort
{
    PendingEstimatedInstallPostGuardReceipt ApplyEstimatedInstallMaintenance(
        IEnumerable<ChartFile> deferredMaintenanceCharts,
        EstimatedInstallDeferredFeedback deferredFeedback);

    PendingEstimatedInstallPostGuardReceipt BuildEstimatedInstallInlineChartInfo(
        IEnumerable<ChartFile> deferredMaintenanceCharts,
        IEnumerable<ChartFile> addedCharts,
        EstimatedInstallDeferredFeedback deferredFeedback);
}

internal interface IPendingEstimatedInstallNotificationPort
{
    UiDialogDefaultResult ShowOperationDialog(
        string messageBoxText,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult);

    void ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded);

    void LogInstallPerformance(string message);

    void LogInstallWarning(Exception exception, string message);
}

internal sealed class PendingEstimatedInstallPostGuardReceipt(
    int affectedCount,
    Action publish,
    ExceptionDispatchInfo failure = null)
{
    private Action publish = publish ?? throw new ArgumentNullException(nameof(publish));

    internal int AffectedCount { get; } = affectedCount;

    internal void ThrowIfFailed()
    {
        failure?.Throw();
    }

    internal void Publish()
    {
        Interlocked.Exchange(ref publish, null)?.Invoke();
    }
}

internal sealed class EstimatedInstallDeferredFeedback
{
    private readonly List<Action<IPendingEstimatedInstallNotificationPort>> notifications = [];

    private readonly BufferedDialogService dialogService;

    internal EstimatedInstallDeferredFeedback()
    {
        dialogService = new BufferedDialogService(this);
    }

    internal IBmsLibraryDialogService DialogService => dialogService;

    internal void LogInstallPerformance(string message)
    {
        notifications.Add(port => port.LogInstallPerformance(message));
    }

    internal void LogInstallWarning(Exception exception, string message)
    {
        notifications.Add(port => port.LogInstallWarning(exception, message));
    }

    internal void ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded)
    {
        notifications.Add(port => port.ShowEstimatedCleanupOnlyCompletedWarning(cleanupOnlySucceeded));
    }

    internal void PublishTo(IPendingEstimatedInstallNotificationPort notificationPort)
    {
        Action<IPendingEstimatedInstallNotificationPort>[] current = [.. notifications];
        notifications.Clear();
        List<Exception> failures = [];
        foreach (Action<IPendingEstimatedInstallNotificationPort> notification in current)
        {
            try
            {
                notification(notificationPort);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("Deferred estimated-install feedback publication failed.", failures);
        }
    }

    private sealed class BufferedDialogService(EstimatedInstallDeferredFeedback owner) : IBmsLibraryDialogService
    {
        private readonly EstimatedInstallDeferredFeedback owner = owner;

        public UiDialogDefaultResult Show(
            string messageBoxText,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            owner.notifications.Add(port => port.ShowOperationDialog(
                messageBoxText,
                caption,
                button,
                icon,
                defaultResult));
            return defaultResult;
        }
    }
}

internal sealed class EstimatedInstallBatchApplyContext
{
    public List<ChartFile> AddedCharts { get; } = [];

    public List<BMSFile> AddedBmsFiles { get; } = [];

    public HashSet<string> AffectedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddInstalledTargets(ChartStorageTargetSet addedTargets, string destinationDirectory)
    {
        AddedCharts.AddRange((addedTargets?.Charts ?? []).Where(chart => chart != null));
        AddedBmsFiles.AddRange(addedTargets?.BmsFiles ?? []);
        AddAffectedDirectory(destinationDirectory);
        foreach (ChartFile addedChart in addedTargets?.Charts ?? [])
        {
            AddAffectedDirectory(DirectoryExt.GetDirectoryNameSimple(addedChart.Path));
        }
    }

    private void AddAffectedDirectory(string directoryPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            AffectedDirectories.Add(directoryPath);
        }
    }
}

internal sealed class PendingEstimatedInstallCollectionApplyResult
{
    public int Before { get; set; }

    public int Changed { get; set; }

    public int After { get; set; }
}

internal sealed class PendingEstimatedInstallExecutionReceipt
{
    internal Stopwatch TotalStopwatch { get; init; }

    internal PendingInstallBatchPlan InstallPlan { get; init; }

    internal PendingInstallBatchResult BatchResult { get; init; }

    internal EstimatedInstallBatchApplyContext BatchApplyContext { get; init; }

    internal PendingEstimatedInstallCollectionApplyResult PendingApplyResult { get; init; }

    internal PendingEstimatedInstallCollectionApplyResult InstalledApplyResult { get; init; }

    internal PendingEstimatedInstallCatalogApplyReceipt CatalogApplyReceipt { get; init; }

    internal PendingEstimatedInstallPostGuardReceipt MaintenanceReceipt { get; set; }

    internal PendingEstimatedInstallPostGuardReceipt InlineChartInfoReceipt { get; set; }

    internal long LibraryStateApplyMs { get; init; }

    internal long PendingApplyMs { get; init; }

    internal long InstalledApplyMs { get; init; }

    internal bool DeletePendingPackageSourceAfterInstall { get; init; }

    internal bool IsSkipped => BatchResult == null;
}

internal sealed class PendingEstimatedInstallExecutionContext
{
    private readonly List<IDisposable> packageEntryNotificationDeferrals = [];

    internal EstimatedInstallDeferredFeedback DeferredFeedback { get; } = new();

    internal PendingEstimatedInstallCatalogApplyReceipt CatalogApplyReceipt { get; set; }

    internal void DeferPackageEntryNotifications(IEnumerable<ChartPackage> packages)
    {
        foreach (PackageChartEntry entry in (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries ?? [])
            .Where(entry => entry != null)
            .Distinct())
        {
            packageEntryNotificationDeferrals.Add(entry.DeferPropertyChangedNotifications());
        }
    }

    internal void PublishDeferredPackageEntryNotifications()
    {
        List<Exception> failures = [];
        for (int index = packageEntryNotificationDeferrals.Count - 1; index >= 0; index--)
        {
            try
            {
                packageEntryNotificationDeferrals[index]?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        packageEntryNotificationDeferrals.Clear();
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Deferred package-entry notification publication failed.",
                failures);
        }
    }
}

internal sealed class PendingEstimatedInstallMutationLease : IDisposable
{
    private IDisposable[] guards;

    private PendingEstimatedInstallMutationLease(IDisposable[] guards)
    {
        this.guards = guards ?? [];
    }

    internal static PendingEstimatedInstallMutationLease Acquire(params Func<IDisposable>[] guardFactories)
    {
        var acquired = new List<IDisposable>();
        try
        {
            foreach (Func<IDisposable> guardFactory in guardFactories ?? [])
            {
                acquired.Add(guardFactory?.Invoke());
            }
            return new PendingEstimatedInstallMutationLease([.. acquired]);
        }
        catch (Exception acquisitionException)
        {
            List<Exception> cleanupFailures = DisposeAll(acquired);
            if (cleanupFailures.Count == 0)
            {
                throw;
            }

            var failures = new List<Exception> { acquisitionException };
            failures.AddRange(cleanupFailures);
            throw new AggregateException(
                "Pending estimated-install mutation lease acquisition and cleanup failed.",
                failures);
        }
    }

    public void Dispose()
    {
        IDisposable[] current = Interlocked.Exchange(ref guards, null);
        if (current == null)
        {
            return;
        }

        List<Exception> failures = DisposeAll(current);
        if (failures.Count > 0)
        {
            throw new AggregateException("Pending estimated-install mutation lease disposal failed.", failures);
        }
    }

    private static List<Exception> DisposeAll(IEnumerable<IDisposable> disposables)
    {
        List<Exception> failures = [];
        IDisposable[] current = [.. disposables ?? []];
        for (int index = current.Length - 1; index >= 0; index--)
        {
            try
            {
                current[index]?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        return failures;
    }
}

internal sealed class PendingEstimatedInstallOwner
{
    private readonly BmsLibraryPackageInstallService packageInstallService;

    private readonly IPendingEstimatedInstallPreparationPort preparationPort;

    private readonly IPendingEstimatedInstallCatalogPort catalogPort;

    private readonly IPendingEstimatedInstallMaintenancePort maintenancePort;

    private readonly IPendingEstimatedInstallNotificationPort notificationPort;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private readonly PackageLifecycleOwner packageLifecycleOwner;

    internal PendingEstimatedInstallOwner(
        BmsLibraryPackageInstallService packageInstallService,
        IPendingEstimatedInstallPreparationPort preparationPort,
        IPendingEstimatedInstallCatalogPort catalogPort,
        PackageLifecycleOwner packageLifecycleOwner,
        IPendingEstimatedInstallMaintenancePort maintenancePort,
        IPendingEstimatedInstallNotificationPort notificationPort,
        ResourceHealthIndexOwner resourceHealthOwner
        )
    {
        this.packageInstallService = packageInstallService ?? throw new ArgumentNullException(nameof(packageInstallService));
        this.preparationPort = preparationPort ?? throw new ArgumentNullException(nameof(preparationPort));
        this.catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
        this.packageLifecycleOwner = packageLifecycleOwner ?? throw new ArgumentNullException(nameof(packageLifecycleOwner));
        this.maintenancePort = maintenancePort ?? throw new ArgumentNullException(nameof(maintenancePort));
        this.notificationPort = notificationPort ?? throw new ArgumentNullException(nameof(notificationPort));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
    }

    internal PendingEstimatedInstallExecutionReceipt InstallPendingPackagesToEstimatedDestinations(
        IEnumerable<ChartPackage> packages,
        PendingEstimatedInstallExecutionContext executionContext)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }

        var totalStopwatch = Stopwatch.StartNew();
        if (executionContext == null)
        {
            throw new ArgumentNullException(nameof(executionContext));
        }
        EstimatedInstallDeferredFeedback deferredFeedback = executionContext.DeferredFeedback;
        BmsLibraryOptionsSnapshot options;
        PendingInstallBatchPlan installPlan;
        using (preparationPort.EnterSnapshotLease())
        {
            options = preparationPort.CurrentOptionsSnapshot;
            installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                packages,
                preparationPort.PendingPackageSnapshot,
                preparationPort.CreateInstalledChartKeySnapshotForEstimatedInstall(deferredFeedback),
                options.DeletePendingPackageSourceAfterInstall,
                preparationPort.CountComponentMoveTargetsForPackage);
        }
        bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
        if (installPlan.SelectedPendingPackages.Count == 0)
        {
            totalStopwatch.Stop();
            deferredFeedback.LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
            return CreateSkippedReceipt(totalStopwatch, installPlan, deletePendingPackageSourceAfterInstall);
        }
        if (installPlan.Groups.Count == 0 && installPlan.DeferredManualHoldCount > 0)
        {
            totalStopwatch.Stop();
            deferredFeedback.LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=deferred_manual_merge_hold deferredManualHold=" + installPlan.DeferredManualHoldCount + " selected=" + installPlan.SelectedPendingCount + " filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
            return CreateSkippedReceipt(totalStopwatch, installPlan, deletePendingPackageSourceAfterInstall);
        }

        executionContext.DeferPackageEntryNotifications(installPlan.SelectedPendingPackages);
        deferredFeedback.LogInstallPerformance("install_pending_packages_to_estimated_destinations start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
        var batchApplyContext = new EstimatedInstallBatchApplyContext();
        PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
            installPlan,
            deletePendingPackageSourceAfterInstall,
            (installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) => preparationPort.InstallChartPackagesForEstimatedInstall(installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall, batchApplyContext, deferredFeedback),
            preparationPort.CreateInstalledDisplayPackageForResourceOnlyMerge,
            package => preparationPort.TryCleanupPendingPackageSourceForEstimatedInstall(
                package,
                deferredFeedback),
            deferredFeedback.LogInstallPerformance);

        PendingEstimatedInstallCatalogPreparation catalogPreparation =
            catalogPort.PrepareEstimatedInstallBatchLibraryState(batchApplyContext, deferredFeedback);
        long libraryStateApplyMs;
        long pendingApplyMs;
        long installedApplyMs;
        PendingEstimatedInstallCollectionApplyResult pendingApplyResult;
        PendingEstimatedInstallCollectionApplyResult installedApplyResult;
        PendingEstimatedInstallCatalogApplyReceipt catalogApplyReceipt;
        using (preparationPort.EnterApplyLease())
        {
            var libraryStateApplyStopwatch = Stopwatch.StartNew();
            bool canUseResourceHealthIndexDelta = resourceHealthOwner.IsCurrent();
            using (canUseResourceHealthIndexDelta ? resourceHealthOwner.SuppressInvalidation() : null)
            {
                catalogApplyReceipt = catalogPort.ApplyEstimatedInstallBatchLibraryState(
                    batchApplyContext,
                    catalogPreparation,
                    deferredFeedback);
                executionContext.CatalogApplyReceipt = catalogApplyReceipt;
            }
            libraryStateApplyStopwatch.Stop();
            libraryStateApplyMs = libraryStateApplyStopwatch.ElapsedMilliseconds;

            if (catalogApplyReceipt.HasFailure)
            {
                pendingApplyResult = new PendingEstimatedInstallCollectionApplyResult();
                installedApplyResult = new PendingEstimatedInstallCollectionApplyResult();
                pendingApplyMs = 0L;
                installedApplyMs = 0L;
            }
            else
            {
                var pendingApplyStopwatch = Stopwatch.StartNew();
                pendingApplyResult = ApplyPendingPackageMutation(
                    batchResult.PendingPackagesToRemove,
                    batchResult.InstallRowsToDelete);
                pendingApplyStopwatch.Stop();
                pendingApplyMs = pendingApplyStopwatch.ElapsedMilliseconds;

                var installedApplyStopwatch = Stopwatch.StartNew();
                installedApplyResult = MergeDeferredInstalledPackages(batchResult.DeferredInstalledPackages);
                installedApplyStopwatch.Stop();
                installedApplyMs = installedApplyStopwatch.ElapsedMilliseconds;
            }
        }

        return new PendingEstimatedInstallExecutionReceipt
        {
            TotalStopwatch = totalStopwatch,
            InstallPlan = installPlan,
            BatchResult = batchResult,
            BatchApplyContext = batchApplyContext,
            PendingApplyResult = pendingApplyResult,
            InstalledApplyResult = installedApplyResult,
            CatalogApplyReceipt = catalogApplyReceipt,
            LibraryStateApplyMs = libraryStateApplyMs,
            PendingApplyMs = pendingApplyMs,
            InstalledApplyMs = installedApplyMs,
            DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall
        };
    }

    internal void CompletePendingPackagesToEstimatedDestinationsUnderGuard(
        PendingEstimatedInstallExecutionReceipt receipt,
        EstimatedInstallDeferredFeedback deferredFeedback)
    {
        if (receipt == null)
        {
            throw new ArgumentNullException(nameof(receipt));
        }
        if (receipt.IsSkipped)
        {
            return;
        }

        catalogPort.CompleteEstimatedInstallBatchLibraryStateUnderGuard(receipt.CatalogApplyReceipt);
        var maintenanceStopwatch = Stopwatch.StartNew();
        receipt.MaintenanceReceipt = maintenancePort.ApplyEstimatedInstallMaintenance(
            receipt.BatchResult.DeferredMaintenanceCharts,
            deferredFeedback);
        receipt.MaintenanceReceipt.ThrowIfFailed();
        receipt.InlineChartInfoReceipt = maintenancePort.BuildEstimatedInstallInlineChartInfo(
            receipt.BatchResult.DeferredMaintenanceCharts,
            receipt.BatchApplyContext.AddedCharts,
            deferredFeedback);
        receipt.InlineChartInfoReceipt.ThrowIfFailed();
        maintenanceStopwatch.Stop();

        if (receipt.DeletePendingPackageSourceAfterInstall && receipt.BatchResult.CleanupOnlySucceeded > 0)
        {
            deferredFeedback.ShowEstimatedCleanupOnlyCompletedWarning(receipt.BatchResult.CleanupOnlySucceeded);
        }

        receipt.TotalStopwatch.Stop();
        deferredFeedback.LogInstallPerformance("install_pending_packages_to_estimated_destinations end libraryStateApplyMs=" + receipt.LibraryStateApplyMs + " pendingApplyMs=" + receipt.PendingApplyMs + " pendingBeforeApply=" + receipt.PendingApplyResult.Before + " pendingRemovedTotal=" + receipt.PendingApplyResult.Changed + " pendingAfterApply=" + receipt.PendingApplyResult.After + " installedApplyMs=" + receipt.InstalledApplyMs + " installedBeforeApply=" + receipt.InstalledApplyResult.Before + " installedAddedTotal=" + receipt.InstalledApplyResult.Changed + " installedAfterApply=" + receipt.InstalledApplyResult.After + " maintenanceTargets=" + receipt.MaintenanceReceipt.AffectedCount + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + receipt.InstallPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + receipt.BatchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + receipt.BatchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + receipt.BatchResult.CleanupOnlyMissingSource + " deferredManualHold=" + receipt.InstallPlan.DeferredManualHoldCount + " totalMs=" + receipt.TotalStopwatch.ElapsedMilliseconds);
    }

    private static PendingEstimatedInstallExecutionReceipt CreateSkippedReceipt(
        Stopwatch totalStopwatch,
        PendingInstallBatchPlan installPlan,
        bool deletePendingPackageSourceAfterInstall)
    {
        return new PendingEstimatedInstallExecutionReceipt
        {
            TotalStopwatch = totalStopwatch,
            InstallPlan = installPlan,
            DeletePendingPackageSourceAfterInstall = deletePendingPackageSourceAfterInstall
        };
    }

    internal void PublishDeferredFeedback(EstimatedInstallDeferredFeedback deferredFeedback)
    {
        deferredFeedback?.PublishTo(notificationPort);
    }

    internal void CompleteCatalogApplyUnderGuard(PendingEstimatedInstallExecutionContext executionContext)
    {
        if (executionContext?.CatalogApplyReceipt != null)
        {
            catalogPort.CompleteEstimatedInstallBatchLibraryStateUnderGuard(executionContext.CatalogApplyReceipt);
        }
    }

    internal void PublishPostGuardEffects(
        PendingEstimatedInstallExecutionReceipt receipt,
        PendingEstimatedInstallExecutionContext executionContext)
    {
        List<Exception> failures = [];
        PublishPostGuardEffect(
            () => catalogPort.PublishEstimatedInstallBatchLibraryState(executionContext?.CatalogApplyReceipt),
            failures);
        PublishPostGuardEffect(() => receipt?.MaintenanceReceipt?.Publish(), failures);
        PublishPostGuardEffect(() => receipt?.InlineChartInfoReceipt?.Publish(), failures);
        PublishPostGuardEffect(
            () => executionContext?.PublishDeferredPackageEntryNotifications(),
            failures);
        if (failures.Count > 0)
        {
            throw new AggregateException("Estimated-install post-guard publication failed.", failures);
        }
    }

    private static void PublishPostGuardEffect(Action publish, ICollection<Exception> failures)
    {
        try
        {
            publish();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private PendingEstimatedInstallCollectionApplyResult ApplyPendingPackageMutation(
        IReadOnlyCollection<ChartPackage> packagesToRemove,
        IEnumerable<string> installRowsToDelete)
    {
        int pendingCountBeforeApply = packageLifecycleOwner.PendingPackages.Count;
        int pendingRemovedTotal = packagesToRemove?.Count ?? 0;
        PendingPackageMutationDelta delta = packageInstallService.BuildPendingPackageMutationDelta(
            packageLifecycleOwner.PendingPackages,
            packagesToRemove: packagesToRemove);
        List<string> installPathsToDelete = [.. delta.InstallPathsToDelete
            .Concat(installRowsToDelete ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
        delta.InstallPathsToDelete = installPathsToDelete;
        delta.HasChanges = delta.HasChanges || installPathsToDelete.Count > 0;
        if (delta.HasChanges)
        {
            packageLifecycleOwner.ApplyPendingPackageMutationDelta(delta);
        }

        return new PendingEstimatedInstallCollectionApplyResult
        {
            Before = pendingCountBeforeApply,
            Changed = pendingRemovedTotal,
            After = packageLifecycleOwner.PendingPackages.Count
        };
    }

    private PendingEstimatedInstallCollectionApplyResult MergeDeferredInstalledPackages(
        IReadOnlyCollection<ChartPackage> deferredInstalledPackages)
    {
        int installedCountBeforeApply = packageLifecycleOwner.InstalledPackages.Count;
        int installedAddedTotal = deferredInstalledPackages?.Count ?? 0;
        if (installedAddedTotal > 0)
        {
            List<ChartPackage> mergedInstalled = [.. packageLifecycleOwner.InstalledPackages.Where(pkg => pkg != null)];
            var installedSet = new HashSet<ChartPackage>(mergedInstalled);
            foreach (ChartPackage installedPackage in deferredInstalledPackages)
            {
                if (installedPackage != null && installedSet.Add(installedPackage))
                {
                    mergedInstalled.Add(installedPackage);
                }
            }
            packageLifecycleOwner.ReplaceInstalledPackages(mergedInstalled);
        }

        return new PendingEstimatedInstallCollectionApplyResult
        {
            Before = installedCountBeforeApply,
            Changed = installedAddedTotal,
            After = packageLifecycleOwner.InstalledPackages.Count
        };
    }
}
