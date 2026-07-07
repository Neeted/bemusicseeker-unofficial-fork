using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Livet;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPendingEstimatedInstallHost
{
    BmsLibraryOptionsSnapshot IPendingEstimatedInstallHost.CurrentOptionsSnapshot
    {
        get
        {
            return CurrentOptionsSnapshot;
        }
    }

    IEnumerable<ChartPackage> IPendingEstimatedInstallHost.PendingPackages
    {
        get
        {
            return ChartPackagesPending;
        }
    }

    bool IPendingEstimatedInstallHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    IDisposable IPendingEstimatedInstallHost.AcquireBmsFilesInitializedAllReaderGuard()
    {
        return rwlockBMSFilesInitializedAll.GetReaderGuard();
    }

    IDisposable IPendingEstimatedInstallHost.AcquirePendingInstallChartsWriterGuard()
    {
        return rwlockPendingInstallCharts.GetWriterGuard();
    }

    IDisposable IPendingEstimatedInstallHost.AcquireBmsFilesWriterGuard()
    {
        return rwlockBMSFiles.GetWriterGuard();
    }

    IDisposable IPendingEstimatedInstallHost.AcquireSongDbInstallWriterGuard()
    {
        return rwlockSongDBInstall.GetWriterGuard();
    }

    IPrimaryHashLookup IPendingEstimatedInstallHost.CreateInstalledChartKeySnapshotForEstimatedInstall()
    {
        return CreateInstalledChartKeySnapshotExcludingChartsUnsafe([], "install_pending_estimated_filter", 0L);
    }

    int IPendingEstimatedInstallHost.CountComponentMoveTargetsForPackage(ChartPackage package, string destinationDirectory, ISet<string> excludedComponentPaths)
    {
        return CountComponentMoveTargetsForPackage(package, destinationDirectory, excludedComponentPaths);
    }

    List<ChartPackage> IPendingEstimatedInstallHost.InstallChartPackagesForEstimatedInstall(
        IEnumerable<ChartPackage> installPackages,
        string destinationDirectory,
        List<ChartFile> deferredMaintenanceCharts,
        List<ChartPackage> deferredInstalledPackages,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage,
        IPrimaryHashLookup existingHashes,
        bool skipInstalledPackageWhenNoBms,
        bool deleteSourceContentsAfterSuccessfulInstall,
        EstimatedInstallBatchApplyContext batchApplyContext)
    {
        return installChartPackages(
            installPackages,
            destinationDirectory,
            deferredMaintenanceCharts,
            deferredInstalledPackages,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall,
            batchApplyContext);
    }

    ChartPackage IPendingEstimatedInstallHost.CreateInstalledDisplayPackageForResourceOnlyMerge(ChartPackage originalPackage, string destinationDirectory)
    {
        return CreateInstalledDisplayPackageForResourceOnlyMerge(originalPackage, destinationDirectory);
    }

    (bool Success, CleanupSourceKind SourceKind) IPendingEstimatedInstallHost.TryCleanupPendingPackageSourceForEstimatedInstall(ChartPackage package)
    {
        bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(package, out CleanupSourceKind sourceKind);
        return (cleanupSucceeded, sourceKind);
    }

    void IPendingEstimatedInstallHost.DeleteInstallRows(IEnumerable<string> paths)
    {
        dbGateway.DeleteInstallRows(paths);
    }

    bool IPendingEstimatedInstallHost.IsResourceHealthIndexCurrent()
    {
        return IsResourceHealthIndexCurrent();
    }

    void IPendingEstimatedInstallHost.ApplyEstimatedInstallBatchLibraryState(EstimatedInstallBatchApplyContext context, bool suppressResourceHealthInvalidation)
    {
        using (suppressResourceHealthInvalidation ? SuppressResourceHealthIndexInvalidation() : null)
        {
            ApplyEstimatedInstallBatchLibraryState(context);
        }
    }

    PendingEstimatedInstallCollectionApplyResult IPendingEstimatedInstallHost.ApplyPendingPackageRemovals(IReadOnlyCollection<ChartPackage> packagesToRemove)
    {
        int pendingCountBeforeApply = ChartPackagesPending.Count;
        int pendingRemovedTotal = packagesToRemove.Count;
        if (pendingRemovedTotal > 0)
        {
            List<ChartPackage> remainingPending = [.. ChartPackagesPending.Where(pkg => pkg != null && !packagesToRemove.Contains(pkg))];
            ChartPackagesPending = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(remainingPending), DispatcherHelper.UIDispatcher);
        }

        return new PendingEstimatedInstallCollectionApplyResult
        {
            Before = pendingCountBeforeApply,
            Changed = pendingRemovedTotal,
            After = ChartPackagesPending.Count
        };
    }

    PendingEstimatedInstallCollectionApplyResult IPendingEstimatedInstallHost.MergeDeferredInstalledPackages(IReadOnlyCollection<ChartPackage> deferredInstalledPackages)
    {
        int installedCountBeforeApply = ChartPackagesInstalled.Count;
        int installedAddedTotal = deferredInstalledPackages.Count;
        if (installedAddedTotal > 0)
        {
            List<ChartPackage> mergedInstalled = [.. ChartPackagesInstalled.Where(pkg => pkg != null)];
            var installedSet = new HashSet<ChartPackage>(mergedInstalled);
            foreach (ChartPackage installedPackage in deferredInstalledPackages)
            {
                if (installedPackage != null && installedSet.Add(installedPackage))
                {
                    mergedInstalled.Add(installedPackage);
                }
            }
            ChartPackagesInstalled = new DispatcherCollection<ChartPackage>(new ObservableCollection<ChartPackage>(mergedInstalled), DispatcherHelper.UIDispatcher);
        }

        return new PendingEstimatedInstallCollectionApplyResult
        {
            Before = installedCountBeforeApply,
            Changed = installedAddedTotal,
            After = ChartPackagesInstalled.Count
        };
    }

    int IPendingEstimatedInstallHost.ApplyEstimatedInstallMaintenanceAndInlineChartInfo(IEnumerable<ChartFile> deferredMaintenanceCharts, IEnumerable<ChartFile> addedCharts)
    {
        List<ChartFile> estimatedInstallMaintenanceTargets = BuildEstimatedInstallMaintenanceTargets(deferredMaintenanceCharts);
        if (estimatedInstallMaintenanceTargets.Count > 0)
        {
            setMaintenanceInfo(
                estimatedInstallMaintenanceTargets,
                forceUpdate: true,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
                resourceHealthMutationReason: "install_package_estimated");
        }
        List<ChartFile> estimatedInstallInlineTargets = BuildEstimatedInstallMaintenanceTargets(
            estimatedInstallMaintenanceTargets.Concat(
                CreateAddedBmsonChartProjections(addedCharts)));
        BuildAndPersistInlineChartInfoForInstalledCharts(
            "install_package_estimated_inline",
            estimatedInstallInlineTargets);
        return estimatedInstallMaintenanceTargets.Count;
    }

    void IPendingEstimatedInstallHost.ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded)
    {
        ShowOperationDialog(string.Format(Resources.Warn_estimated_install_cleanup_only_completed, cleanupOnlySucceeded), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
    }

    void IPendingEstimatedInstallHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }
}
