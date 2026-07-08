using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryFixInstallationHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    void RunWithFixInstallationWriteLocks(Action action);

    IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingCharts(IEnumerable<ChartFile> excludedCharts);

    LibraryFixInstallationResult FixInstallationDirectory(
        IEnumerable<ChartFile> charts,
        Func<ChartPackage, string, bool> movePackageFiles,
        Func<ChartFile, bool> confirmDuplicateRemoval);

    bool MoveChartPackageFiles(ChartPackage package, string destinationDirectory, IPrimaryHashLookup existingHashes);

    bool ConfirmDuplicateReinstallSkipped(ChartFile chart);

    void ApplyLibraryMutationDelta(LibraryMutationDelta delta);

    void RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts);

    List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts);

    void SetFixInstallationMaintenanceInfo(IEnumerable<ChartFile> charts);
}

internal static class LibraryFixInstallationCoordinator
{
    internal static void FixInstallationDirectoryCharts(
        ILibraryFixInstallationHost host,
        IEnumerable<ChartFile> charts,
        IEnumerable<string> approvedDuplicateRemovalChartPaths)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (host.TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.FixInstallationDirectoryCharts)))
        {
            return;
        }

        HashSet<string> approvedDuplicateRemovalPaths = approvedDuplicateRemovalChartPaths == null
            ? null
            : new HashSet<string>(approvedDuplicateRemovalChartPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

        host.RunWithFixInstallationWriteLocks(() =>
        {
            List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
            IPrimaryHashLookup existingHashes = host.CreateInstalledChartKeySnapshotExcludingCharts(chartList);
            LibraryFixInstallationResult result = host.FixInstallationDirectory(
                chartList,
                (package, destinationDirectory) => host.MoveChartPackageFiles(package, destinationDirectory, existingHashes),
                chart => approvedDuplicateRemovalPaths != null
                    ? !string.IsNullOrWhiteSpace(chart?.Path) && approvedDuplicateRemovalPaths.Contains(chart.Path)
                    : host.ConfirmDuplicateReinstallSkipped(chart));
            host.ApplyLibraryMutationDelta(result.MutationDelta);
            if (result.ChartsToRemove.Count > 0)
            {
                host.RemoveLibraryCharts(result.ChartsToRemove);
            }
            List<ChartFile> maintenanceTargets = host.NormalizeResourceMaintenanceTargetCharts(result.MaintenanceCharts);
            if (maintenanceTargets.Count > 0)
            {
                host.SetFixInstallationMaintenanceInfo(maintenanceTargets);
            }
        });
    }
}
