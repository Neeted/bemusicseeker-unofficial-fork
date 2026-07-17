using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPendingEstimatedInstallHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    IDisposable AcquireBmsFilesInitializedAllReaderGuard();

    IDisposable AcquirePendingInstallChartsWriterGuard();

    IDisposable AcquireBmsFilesWriterGuard();

    IDisposable AcquireSongDbInstallWriterGuard();

    BmsLibraryOptionsSnapshot CurrentOptionsSnapshot { get; }

    IEnumerable<ChartPackage> PendingPackages { get; }

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

    void ApplyEstimatedInstallBatchLibraryState(EstimatedInstallBatchApplyContext context);

    PendingEstimatedInstallCollectionApplyResult ApplyEstimatedInstallPendingPackageMutation(
        IReadOnlyCollection<ChartPackage> packagesToRemove,
        IEnumerable<string> installRowsToDelete);

    PendingEstimatedInstallCollectionApplyResult MergeDeferredInstalledPackages(IReadOnlyCollection<ChartPackage> deferredInstalledPackages);

    int ApplyEstimatedInstallMaintenance(IEnumerable<ChartFile> deferredMaintenanceCharts);

    void BuildEstimatedInstallInlineChartInfo(IEnumerable<ChartFile> deferredMaintenanceCharts, IEnumerable<ChartFile> addedCharts);

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

internal static class PendingEstimatedInstallCoordinator
{
    internal static void InstallPendingPackagesToEstimatedDestinations(
        BmsLibraryPackageInstallService packageInstallService,
        IPendingEstimatedInstallHost host,
        ResourceHealthIndexOwner resourceHealthOwner,
        IEnumerable<ChartPackage> packages)
    {
        if (packages == null)
        {
            throw new ArgumentNullException(nameof(packages));
        }
        if (resourceHealthOwner == null)
        {
            throw new ArgumentNullException(nameof(resourceHealthOwner));
        }
        if (host.TryBlockLr2SongDbSyncMutation(nameof(InstallPendingPackagesToEstimatedDestinations)))
        {
            return;
        }

        BmsLibraryOptionsSnapshot options = host.CurrentOptionsSnapshot;
        var totalStopwatch = Stopwatch.StartNew();
        using (host.AcquireBmsFilesInitializedAllReaderGuard())
        using (host.AcquirePendingInstallChartsWriterGuard())
        using (host.AcquireBmsFilesWriterGuard())
        using (host.AcquireSongDbInstallWriterGuard())
        {
            bool deletePendingPackageSourceAfterInstall = options.DeletePendingPackageSourceAfterInstall;
            PendingInstallBatchPlan installPlan = packageInstallService.BuildEstimatedInstallBatchPlan(
                packages,
                host.PendingPackages,
                host.CreateInstalledChartKeySnapshotForEstimatedInstall(),
                deletePendingPackageSourceAfterInstall,
                host.CountComponentMoveTargetsForPackage);
            if (installPlan.SelectedPendingPackages.Count == 0)
            {
                totalStopwatch.Stop();
                host.LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=no_pending_target filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                return;
            }
            if (installPlan.Groups.Count == 0 && installPlan.DeferredManualHoldCount > 0)
            {
                totalStopwatch.Stop();
                host.LogInstallPerformance("install_pending_packages_to_estimated_destinations skipped reason=deferred_manual_merge_hold deferredManualHold=" + installPlan.DeferredManualHoldCount + " selected=" + installPlan.SelectedPendingCount + " filterMs=" + installPlan.FilterMs + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                return;
            }

            host.LogInstallPerformance("install_pending_packages_to_estimated_destinations start selected=" + installPlan.SelectedPendingCount + " groups=" + installPlan.Groups.Count + " groupedPackages=" + installPlan.GroupedPackageCount + " installTargets=" + installPlan.InstallTargetFileCount + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " deleteSourceContents=" + deletePendingPackageSourceAfterInstall + " filterMs=" + installPlan.FilterMs + " groupBuildMs=" + installPlan.GroupBuildMs + " planBuildMs=" + installPlan.PlanBuildMs);
            var batchApplyContext = new EstimatedInstallBatchApplyContext();
            PendingInstallBatchResult batchResult = packageInstallService.ExecuteEstimatedInstallBatchPlan(
                installPlan,
                deletePendingPackageSourceAfterInstall,
                (installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall) => host.InstallChartPackagesForEstimatedInstall(installPackages, destinationDirectory, deferredMaintenanceCharts, deferredInstalledPackages, excludedComponentPathsByPackage, existingHashes, skipInstalledPackageWhenNoBms, deleteSourceContentsAfterSuccessfulInstall, batchApplyContext),
                host.CreateInstalledDisplayPackageForResourceOnlyMerge,
                host.TryCleanupPendingPackageSourceForEstimatedInstall,
                host.LogInstallPerformance);
            var libraryStateApplyStopwatch = Stopwatch.StartNew();
            bool canUseResourceHealthIndexDelta = resourceHealthOwner.IsCurrent();
            using (canUseResourceHealthIndexDelta ? resourceHealthOwner.SuppressInvalidation() : null)
            {
                host.ApplyEstimatedInstallBatchLibraryState(batchApplyContext);
            }
            libraryStateApplyStopwatch.Stop();

            var pendingApplyStopwatch = Stopwatch.StartNew();
            PendingEstimatedInstallCollectionApplyResult pendingApplyResult = host.ApplyEstimatedInstallPendingPackageMutation(
                batchResult.PendingPackagesToRemove,
                batchResult.InstallRowsToDelete);
            pendingApplyStopwatch.Stop();

            var installedApplyStopwatch = Stopwatch.StartNew();
            PendingEstimatedInstallCollectionApplyResult installedApplyResult = host.MergeDeferredInstalledPackages(batchResult.DeferredInstalledPackages);
            installedApplyStopwatch.Stop();

            var maintenanceStopwatch = Stopwatch.StartNew();
            int estimatedInstallMaintenanceTargetCount = host.ApplyEstimatedInstallMaintenance(
                batchResult.DeferredMaintenanceCharts);
            host.BuildEstimatedInstallInlineChartInfo(
                batchResult.DeferredMaintenanceCharts,
                batchApplyContext.AddedCharts);
            maintenanceStopwatch.Stop();

            if (deletePendingPackageSourceAfterInstall && batchResult.CleanupOnlySucceeded > 0)
            {
                host.ShowEstimatedCleanupOnlyCompletedWarning(batchResult.CleanupOnlySucceeded);
            }

            totalStopwatch.Stop();
            host.LogInstallPerformance("install_pending_packages_to_estimated_destinations end libraryStateApplyMs=" + libraryStateApplyStopwatch.ElapsedMilliseconds + " pendingApplyMs=" + pendingApplyStopwatch.ElapsedMilliseconds + " pendingBeforeApply=" + pendingApplyResult.Before + " pendingRemovedTotal=" + pendingApplyResult.Changed + " pendingAfterApply=" + pendingApplyResult.After + " installedApplyMs=" + installedApplyStopwatch.ElapsedMilliseconds + " installedBeforeApply=" + installedApplyResult.Before + " installedAddedTotal=" + installedApplyResult.Changed + " installedAfterApply=" + installedApplyResult.After + " maintenanceTargets=" + estimatedInstallMaintenanceTargetCount + " maintenanceMs=" + maintenanceStopwatch.ElapsedMilliseconds + " cleanupOnlyCandidates=" + installPlan.CleanupOnlyCandidates.Count + " cleanupOnlySucceeded=" + batchResult.CleanupOnlySucceeded + " cleanupOnlyFailed=" + batchResult.CleanupOnlyFailed + " cleanupOnlyMissingSource=" + batchResult.CleanupOnlyMissingSource + " deferredManualHold=" + installPlan.DeferredManualHoldCount + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
        }
    }
}
