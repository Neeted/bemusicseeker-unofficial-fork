using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPendingEstimatedInstallPreparationPort
{
    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    IReadOnlyCollection<ChartPackage> PendingPackageSnapshot { get; }

    IDisposable EnterMutationLease();

    IPrimaryHashLookup CreateInstalledChartKeySnapshotForEstimatedInstall();

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
        EstimatedInstallBatchApplyContext batchApplyContext);

    ChartPackage CreateInstalledDisplayPackageForResourceOnlyMerge(ChartPackage originalPackage, string destinationDirectory);

    (bool Success, CleanupSourceKind SourceKind) TryCleanupPendingPackageSourceForEstimatedInstall(ChartPackage package);
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
    void ApplyEstimatedInstallBatchLibraryState(EstimatedInstallBatchApplyContext context);
}

internal interface IPendingEstimatedInstallMaintenancePort
{
    int ApplyEstimatedInstallMaintenance(IEnumerable<ChartFile> deferredMaintenanceCharts);

    void BuildEstimatedInstallInlineChartInfo(IEnumerable<ChartFile> deferredMaintenanceCharts, IEnumerable<ChartFile> addedCharts);
}

internal interface IPendingEstimatedInstallNotificationPort
{
    void ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded);

    void LogInstallPerformance(string message);
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

    internal void InstallPendingPackagesToEstimatedDestinations(IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }

        var totalStopwatch = Stopwatch.StartNew();
        using (preparationPort.EnterMutationLease())
        {
            BmsLibraryOptionsSnapshot options = preparationPort.CurrentOptionsSnapshot;
            bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
            PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                packages,
                preparationPort.PendingPackageSnapshot,
                preparationPort.CreateInstalledChartKeySnapshotForEstimatedInstall(),
                deletePendingPackageSourceAfterInstall,
                preparationPort.CountComponentMoveTargetsForPackage);
            if (installPlan.SelectedPendingPackages.Count == 0)
            {
                totalStopwatch.Stop();
                notificationPort.LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                return;
            }
            if (installPlan.Groups.Count == 0 && installPlan.DeferredManualHoldCount > 0)
            {
                totalStopwatch.Stop();
                notificationPort.LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=deferred_manual_merge_hold deferredManualHold=" + installPlan.DeferredManualHoldCount + " selected=" + installPlan.SelectedPendingCount + " filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                return;
            }

            notificationPort.LogInstallPerformance("install_pending_packages_to_estimated_destinations start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
            var batchApplyContext = new EstimatedInstallBatchApplyContext();
            PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
                installPlan,
                deletePendingPackageSourceAfterInstall,
                (installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) => preparationPort.InstallChartPackagesForEstimatedInstall(installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall, batchApplyContext),
                preparationPort.CreateInstalledDisplayPackageForResourceOnlyMerge,
                preparationPort.TryCleanupPendingPackageSourceForEstimatedInstall,
                notificationPort.LogInstallPerformance);
            var libraryStateApplyStopwatch = Stopwatch.StartNew();
            bool canUseResourceHealthIndexDelta = resourceHealthOwner.IsCurrent();
            using (canUseResourceHealthIndexDelta ? resourceHealthOwner.SuppressInvalidation() : null)
            {
                catalogPort.ApplyEstimatedInstallBatchLibraryState(batchApplyContext);
            }
            libraryStateApplyStopwatch.Stop();

            var pendingApplyStopwatch = Stopwatch.StartNew();
            PendingEstimatedInstallCollectionApplyResult pendingApplyResult = ApplyPendingPackageMutation(
                batchResult.PendingPackagesToRemove,
                batchResult.InstallRowsToDelete);
            pendingApplyStopwatch.Stop();

            var installedApplyStopwatch = Stopwatch.StartNew();
            PendingEstimatedInstallCollectionApplyResult installedApplyResult = MergeDeferredInstalledPackages(batchResult.DeferredInstalledPackages);
            installedApplyStopwatch.Stop();

            var maintenanceStopwatch = Stopwatch.StartNew();
            int estimatedInstallMaintenanceTargetCount = maintenancePort.ApplyEstimatedInstallMaintenance(
                batchResult.DeferredMaintenanceCharts);
            maintenancePort.BuildEstimatedInstallInlineChartInfo(
                batchResult.DeferredMaintenanceCharts,
                batchApplyContext.AddedCharts);
            maintenanceStopwatch.Stop();

            if (deletePendingPackageSourceAfterInstall && batchResult.CleanupOnlySucceeded > 0)
            {
                notificationPort.ShowEstimatedCleanupOnlyCompletedWarning(batchResult.CleanupOnlySucceeded);
            }

            totalStopwatch.Stop();
            notificationPort.LogInstallPerformance("install_pending_packages_to_estimated_destinations end libraryStateApplyMs=" + libraryStateApplyStopwatch.ElapsedMilliseconds + " pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingApplyResult.Before + " pendingRemovedTotal=" + pendingApplyResult.Changed + " pendingAfterApply=" + pendingApplyResult.After + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedApplyResult.Before + " installedAddedTotal=" + installedApplyResult.Changed + " installedAfterApply=" + installedApplyResult.After + " maintenanceTargets=" + estimatedInstallMaintenanceTargetCount + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + installPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + batchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + batchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + batchResult.CleanupOnlyMissingSource + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
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
