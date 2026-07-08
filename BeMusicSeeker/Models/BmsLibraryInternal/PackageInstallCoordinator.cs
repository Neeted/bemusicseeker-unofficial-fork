using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IPackageInstallHost
{
    void ThrowIfLr2SongDbSyncMutationBlocked(string operation);

    PackageInstallExecutionResult InstallPackages(
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
        bool deleteSourceContentsAfterSuccessfulInstall);

    bool MoveChartPackageFiles(
        ChartPackage package,
        string destinationDirectory,
        bool deleteAllContents,
        IPrimaryHashLookup hashSnapshot,
        ISet<string> excludedComponentPaths);

    void UpsertInstalledChartRows(ChartStorageTargetSet addedTargets);

    void AddDeferredMaintenanceCharts(List<ChartFile> deferredMaintenanceCharts, IEnumerable<ChartFile> addedCharts);

    void SetInstallPackageMaintenanceInfo(IEnumerable<ChartFile> addedCharts);

    void SetBmsScore(IEnumerable<BMSFile> bmsFiles);

    void AddEstimatedInstallBatchTargets(EstimatedInstallBatchApplyContext context, ChartStorageTargetSet addedTargets, string installationDirectory);

    void ApplyInstalledChartStorageTargets(ChartStorageTargetSet addedTargets);

    void AddReverseLookupDirectoriesForInstall(IEnumerable<string> addedDirectories);

    void AddDeferredInstalledPackages(List<ChartPackage> deferredInstalledPackages, IEnumerable<ChartPackage> installedPackages);

    void RegisterInstalledPackages(IEnumerable<ChartPackage> installedPackages);

    void LogInstallPerformance(string message);

    void BuildAndPersistInlineChartInfoForInstalledCharts(string reason, IEnumerable<ChartFile> charts);
}

internal static class PackageInstallCoordinator
{
    internal static List<ChartPackage> InstallChartPackages(
        IPackageInstallHost host,
        IEnumerable<ChartPackage> chartPackagesInstall,
        string installationDirectory = null,
        List<ChartFile> deferredMaintenanceCharts = null,
        List<ChartPackage> deferredInstalledPackages = null,
        Dictionary<ChartPackage, HashSet<string>> excludedComponentPathsByPackage = null,
        IPrimaryHashLookup existingHashes = null,
        bool skipInstalledPackageWhenNoBms = false,
        bool deleteSourceContentsAfterSuccessfulInstall = false,
        EstimatedInstallBatchApplyContext estimatedInstallBatchApplyContext = null)
    {
        host.ThrowIfLr2SongDbSyncMutationBlocked("installChartPackages");
        List<ChartPackage> installPackageList = [.. (chartPackagesInstall ?? []).Where(package => package != null)];
        List<ChartFile> addedChartsForChartInfo = [];

        static ChartStorageTargetSet CreateAddedStorageTargets(PackageInstallExecutionResult installResult)
        {
            return ChartStorageTargetSet.FromCharts(installResult?.AddedCharts);
        }

        void UpsertInstalledChartRows(PackageInstallExecutionResult installResult)
        {
            host.UpsertInstalledChartRows(CreateAddedStorageTargets(installResult));
        }

        void UpdateInstalledChartMaintenance(PackageInstallExecutionResult installResult)
        {
            List<ChartFile> addedCharts = CreateAddedStorageTargets(installResult).Charts;
            if (deferredMaintenanceCharts != null)
            {
                host.AddDeferredMaintenanceCharts(deferredMaintenanceCharts, addedCharts);
                return;
            }
            if (addedCharts.Count > 0)
            {
                host.SetInstallPackageMaintenanceInfo(addedCharts);
            }
        }

        void ApplyInstalledChartState(PackageInstallExecutionResult installResult)
        {
            ChartStorageTargetSet addedTargets = CreateAddedStorageTargets(installResult);
            List<ChartFile> addedCharts = addedTargets.Charts;
            addedChartsForChartInfo.AddRange(addedCharts);
            if (estimatedInstallBatchApplyContext != null)
            {
                host.AddEstimatedInstallBatchTargets(estimatedInstallBatchApplyContext, addedTargets, installationDirectory);
                return;
            }
            host.ApplyInstalledChartStorageTargets(addedTargets);
            host.AddReverseLookupDirectoriesForInstall(addedTargets.GetDistinctChartDirectories());
        }

        PackageInstallExecutionResult result = host.InstallPackages(
            installPackageList,
            installationDirectory,
            (package, destinationDirectory, deleteAllContents, hashSnapshot, excludedComponentPaths) => host.MoveChartPackageFiles(package, destinationDirectory, deleteAllContents, hashSnapshot, excludedComponentPaths),
            UpsertInstalledChartRows,
            UpdateInstalledChartMaintenance,
            installResult => host.SetBmsScore(CreateAddedStorageTargets(installResult).BmsFiles),
            ApplyInstalledChartState,
            excludedComponentPathsByPackage,
            existingHashes,
            skipInstalledPackageWhenNoBms,
            deleteSourceContentsAfterSuccessfulInstall);
        if (estimatedInstallBatchApplyContext != null && result.FailedPackages.Count < installPackageList.Count)
        {
            host.AddEstimatedInstallBatchTargets(estimatedInstallBatchApplyContext, ChartStorageTargetSet.FromCharts([]), installationDirectory);
        }
        if (result.InstalledPackagesToRegister.Count > 0)
        {
            if (deferredInstalledPackages != null)
            {
                host.AddDeferredInstalledPackages(deferredInstalledPackages, result.InstalledPackagesToRegister);
            }
            else
            {
                host.RegisterInstalledPackages(result.InstalledPackagesToRegister);
            }
        }
        host.LogInstallPerformance("install_chart_packages dst=" + (installationDirectory ?? "(auto)") + " packages=" + installPackageList.Count + " addedFiles=" + result.AddedEntries.Count + " failedPackages=" + result.FailedPackages.Count + " deleteSourceContents=" + deleteSourceContentsAfterSuccessfulInstall + " moveMs=" + result.MoveMs + " songDbMs=" + result.SongDbMs + " maintenanceMs=" + result.MaintenanceMs + " scoreMs=" + result.ScoreMs + " applyMs=" + result.ApplyMs + " totalMs=" + result.TotalMs);
        if (deferredMaintenanceCharts == null)
        {
            host.BuildAndPersistInlineChartInfoForInstalledCharts("install_package_inline", addedChartsForChartInfo);
        }
        return result.FailedPackages;
    }
}
