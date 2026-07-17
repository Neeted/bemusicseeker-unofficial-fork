using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPackageInstallHost
{
    void IPackageInstallHost.ThrowIfLr2SongDbSyncMutationBlocked(string operation)
    {
        ThrowIfLr2SongDbSyncMutationBlocked(operation);
    }

    PackageInstallExecutionResult IPackageInstallHost.InstallPackages(
        IEnumerable<ChartPackage> packages,
        string installationDirectory,
        Func<ChartPackage, string, bool, IPrimaryHashLookup, ISet<string>, bool> movePackageFiles,
        Action<PackageInstallExecutionResult> upsertStorageRows,
        Action<PackageInstallExecutionResult> updateMaintenance,
        Action<PackageInstallExecutionResult> applyScores,
        Action<PackageInstallExecutionResult> applyState,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage,
        IPrimaryHashLookup existingHashes,
        bool skipInstalledPackageWhenNoBms,
        bool deleteSourceContentsAfterSuccessfulInstall)
    {
        return packageInstallService.InstallPackages(
            packages,
            installationDirectory,
            movePackageFiles,
            upsertStorageRows,
            updateMaintenance,
            applyScores,
            applyState,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall);
    }

    bool IPackageInstallHost.MoveChartPackageFiles(
        ChartPackage package,
        string destinationDirectory,
        bool deleteAllContents,
        IPrimaryHashLookup hashSnapshot,
        ISet<string> excludedComponentPaths)
    {
        return MoveChartPackageFiles(package, destinationDirectory, true, deleteAllContents, hashSnapshot, excludedComponentPaths);
    }

    void IPackageInstallHost.UpsertInstalledChartRows(ChartStorageTargetSet addedTargets)
    {
        if (addedTargets.BmsFiles.Count > 0)
        {
            ExecuteLr2SongDbWrite(
                () => dbGateway.UpsertSongs(addedTargets.BmsFiles),
                stage: "lr2_song_db_install_upsert_failed",
                logReason: "install_package");
        }
        if (addedTargets.BmsonSongs.Count > 0)
        {
            dbGateway.UpsertBmsonSongs(addedTargets.BmsonSongs);
        }
    }

    void IPackageInstallHost.AddDeferredMaintenanceCharts(List<ChartFile> deferredMaintenanceCharts, IEnumerable<ChartFile> addedCharts)
    {
        deferredMaintenanceCharts.AddRange(addedCharts);
    }

    void IPackageInstallHost.ApplyInstallPackageMaintenance(IEnumerable<ChartFile> addedCharts)
    {
        ApplyCatalogMaintenance(
            addedCharts,
            forceUpdate: true,
            resourceHealthMutationReason: "install_package");
    }

    void IPackageInstallHost.SetBmsScore(IEnumerable<BMSFile> bmsFiles)
    {
        SetBMSScore(bmsFiles);
    }

    void IPackageInstallHost.AddEstimatedInstallBatchTargets(EstimatedInstallBatchApplyContext context, ChartStorageTargetSet addedTargets, string installationDirectory)
    {
        context.AddInstalledTargets(addedTargets, installationDirectory);
    }

    void IPackageInstallHost.ApplyInstalledChartStorageTargets(ChartStorageTargetSet addedTargets)
    {
        ApplyInstalledChartStorageTargets(addedTargets, "install_package");
    }

    void IPackageInstallHost.AddReverseLookupDirectoriesForInstall(IEnumerable<string> addedDirectories)
    {
        List<string> directoryList = [.. addedDirectories ?? []];
        if (directoryList.Count == 0)
        {
            return;
        }
        if (ChartDirectoryScanBuilder.TryBuildFromRoots(directoryList, out ChartScanResult addedDirectoryScan, out string scanFailureReason))
        {
            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            foreach (string dir in addedDirectoryScan.ChartDirectories)
            {
                reverseLookupMutation = reverseLookupMutation.Combine(directoryResourceLookupCache.AddDir(dir, addedDirectoryScan));
            }
            LogReverseLookupMutationAndQueueWarmupIfNeeded("install_package", reverseLookupMutation);
        }
        else
        {
            LogInstallPerformanceWarn("install_package resource_cache_update skipped reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown") + " dirs=" + directoryList.Count);
        }
    }

    void IPackageInstallHost.AddDeferredInstalledPackages(List<ChartPackage> deferredInstalledPackages, IEnumerable<ChartPackage> installedPackages)
    {
        deferredInstalledPackages.AddRange(installedPackages);
    }

    void IPackageInstallHost.RegisterInstalledPackages(IEnumerable<ChartPackage> installedPackages)
    {
        packageLifecycleOwner.AddInstalledPackages(installedPackages);
    }

    void IPackageInstallHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }

    void IPackageInstallHost.BuildAndPersistInlineChartInfoForInstalledCharts(string reason, IEnumerable<ChartFile> charts)
    {
        BuildAndPersistInlineChartInfoForInstalledCharts(reason, charts);
    }

    void IPackageInstallHost.ApplyEstimatedInstallBatchStorageTargets(ChartStorageTargetSet addedTargets)
    {
        ApplyInstalledChartStorageTargets(addedTargets, "install_package_batch");
    }

    bool IPackageInstallHost.TryBuildAddedDirectoryScan(IEnumerable<string> directories, out ChartScanResult scan, out string scanFailureReason)
    {
        return ChartDirectoryScanBuilder.TryBuildFromRoots(directories, out scan, out scanFailureReason);
    }

    DirectoryResourceLookupCache.ReverseLookupMutationResult IPackageInstallHost.AddEstimatedBatchReverseLookupDirectories(IEnumerable<string> directories, ChartScanResult scan)
    {
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string dir in directories ?? [])
        {
            mutation = mutation.Combine(directoryResourceLookupCache.AddDir(dir, scan));
        }
        return mutation;
    }

    void IPackageInstallHost.LogInstallPerformanceWarn(string message)
    {
        LogInstallPerformanceWarn(message);
    }

    void IPackageInstallHost.LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
    {
        LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
    }
}
