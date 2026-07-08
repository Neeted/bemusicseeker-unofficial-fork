using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : ILibraryFixInstallationHost
{
    bool ILibraryFixInstallationHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    void ILibraryFixInstallationHost.RunWithFixInstallationWriteLocks(Action action)
    {
        using (rwlockBMSFilesInitializedAll.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                action();
            }
        }
    }

    IPrimaryHashLookup ILibraryFixInstallationHost.CreateInstalledChartKeySnapshotExcludingCharts(IEnumerable<ChartFile> excludedCharts)
    {
        return CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excludedCharts);
    }

    LibraryFixInstallationResult ILibraryFixInstallationHost.FixInstallationDirectory(
        IEnumerable<ChartFile> charts,
        Func<ChartPackage, string, bool> movePackageFiles,
        Func<ChartFile, bool> confirmDuplicateRemoval)
    {
        return libraryFileOperationsService.FixInstallationDirectory(
            charts,
            movePackageFiles,
            confirmDuplicateRemoval);
    }

    bool ILibraryFixInstallationHost.MoveChartPackageFiles(ChartPackage package, string destinationDirectory, IPrimaryHashLookup existingHashes)
    {
        return MoveChartPackageFiles(
            package,
            destinationDirectory,
            showMessageBoxOnInstallFail: true,
            deleteAllContents: false,
            existingHashes: existingHashes);
    }

    bool ILibraryFixInstallationHost.ConfirmDuplicateReinstallSkipped(ChartFile chart)
    {
        return ShowOperationDialog(
            string.Format(Resources.Confirm_DuplicateReinstallSkipped, chart.Path, string.Join(Environment.NewLine, GetDuplicateInstallRepairPaths(chart))),
            Resources.MessageBoxTitle_Confirm,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    void ILibraryFixInstallationHost.ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        ApplyLibraryMutationDelta(delta);
    }

    void ILibraryFixInstallationHost.RemoveLibraryCharts(IEnumerable<LibraryChartRef> charts)
    {
        RemoveLibraryCharts(charts, approvedWholeFolderDeletePaths: []);
    }

    List<ChartFile> ILibraryFixInstallationHost.NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
    {
        return NormalizeResourceMaintenanceTargetCharts(charts);
    }

    void ILibraryFixInstallationHost.SetFixInstallationMaintenanceInfo(IEnumerable<ChartFile> charts)
    {
        setMaintenanceInfo(
            charts,
            forceUpdate: true,
            resourceHealthMutationReason: "fix_installation_directory");
    }
}
