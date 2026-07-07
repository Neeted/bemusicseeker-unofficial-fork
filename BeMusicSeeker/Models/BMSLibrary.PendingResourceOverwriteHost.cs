using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPendingResourceOverwriteHost
{
    BmsLibraryOptionsSnapshot IPendingResourceOverwriteHost.CurrentOptionsSnapshot
    {
        get
        {
            return CurrentOptionsSnapshot;
        }
    }

    IEnumerable<ChartPackage> IPendingResourceOverwriteHost.PendingPackages
    {
        get
        {
            return ChartPackagesPending;
        }
    }

    int IPendingResourceOverwriteHost.PendingPackageCount
    {
        get
        {
            return ChartPackagesPending.Count;
        }
    }

    IDisposable IPendingResourceOverwriteHost.AcquireBmsFilesInitializedAllReaderGuard()
    {
        return rwlockBMSFilesInitializedAll.GetReaderGuard();
    }

    IDisposable IPendingResourceOverwriteHost.AcquirePendingInstallChartsWriterGuard()
    {
        return rwlockPendingInstallCharts.GetWriterGuard();
    }

    IDisposable IPendingResourceOverwriteHost.AcquireBmsFilesWriterGuard()
    {
        return rwlockBMSFiles.GetWriterGuard();
    }

    IDisposable IPendingResourceOverwriteHost.AcquireSongDbInstallWriterGuard()
    {
        return rwlockSongDBInstall.GetWriterGuard();
    }

    InstalledChartLookupIndexSnapshot IPendingResourceOverwriteHost.CreateInstalledChartLookupSnapshot()
    {
        return CreateInstalledChartLookupSnapshotUnsafe();
    }

    int IPendingResourceOverwriteHost.CountEligiblePackages(IEnumerable<ChartPackage> packages)
    {
        return packageInstallService.DeduplicatePackagesByPathOrReference(packages).Count;
    }

    InstalledOnlyPackageResolutionResult IPendingResourceOverwriteHost.ResolveInstalledOnlyPackageDestination(ChartPackage pendingPackage, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        return CreateInstallEstimationService().TryPrepareInstalledOnlyPackageDestination(pendingPackage, installedDirectoryIndex);
    }

    string IPendingResourceOverwriteHost.DescribeSkipDetail(InstalledOnlyPackageResolutionResult resolution, ChartPackage pendingPackage, IInstalledChartLookupIndex installedDirectoryIndex)
    {
        if (pendingPackage == null)
        {
            return null;
        }
        switch (resolution.Reason)
        {
            case InstalledDirectoryResolveReason.ChartHasMultipleInstalledDirectories:
                ChartFile multipleDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMultipleInstalledDirectories(pendingPackage, installedDirectoryIndex);
                return "advanced_pending_resource_overwrite skip_chart_multi_dst path=" + pendingPackage.path + " chartPath=" + (multipleDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(multipleDirectoryChart) ?? "(null)") + " dirCount=" + ((multipleDirectoryChart == null) ? 0 : BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(installedDirectoryIndex, multipleDirectoryChart).Count);
            case InstalledDirectoryResolveReason.PackageHasSplitInstalledDirectories:
                return "advanced_pending_resource_overwrite skip_package_split_dst path=" + pendingPackage.path + " dirCount=" + BmsLibraryInstallEstimationService.CountDistinctInstalledDirectoriesForPackage(pendingPackage, installedDirectoryIndex);
            default:
                ChartFile missingDirectoryChart = BmsLibraryInstallEstimationService.FindChartWithMissingInstalledDirectory(pendingPackage, installedDirectoryIndex);
                return "advanced_pending_resource_overwrite skip_missing_instl_dst path=" + pendingPackage.path + " chartPath=" + (missingDirectoryChart?.Path ?? "(null)") + " hash=" + (ChartLookupKey.GetPrimaryHash(missingDirectoryChart) ?? "(null)");
        }
    }

    bool IPendingResourceOverwriteHost.HasResourceOverwriteTargetsForInstalledOnlyPackage(ChartPackage package, string destinationDir)
    {
        return HasResourceOverwriteTargetsForInstalledOnlyPackage(package, destinationDir);
    }

    bool IPendingResourceOverwriteHost.InstallPendingPackageToEstimatedDestination(ChartPackage pendingPackage, string destinationDir)
    {
        try
        {
            InstallPendingPackagesToEstimatedDestinations([pendingPackage]);
            return true;
        }
        catch (Exception ex)
        {
            string displayedExceptionMessage = GetDisplayedExceptionMessage(ex);
            NLogWrapper.FileLogger?.Warn(ex, "advanced_pending_resource_overwrite install_failed_exception path=" + pendingPackage.path + " dst=" + destinationDir + " error=" + displayedExceptionMessage);
            ShowOperationDialog(string.Format(Resources.Error_InstallFailed, pendingPackage.path, destinationDir, displayedExceptionMessage), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            return false;
        }
    }

    (bool Success, CleanupSourceKind SourceKind) IPendingResourceOverwriteHost.TryCleanupPendingPackageSourceForEstimatedInstall(ChartPackage package)
    {
        bool cleanupSucceeded = TryCleanupPendingPackageSourceForEstimatedInstall(package, out CleanupSourceKind sourceKind);
        return (cleanupSucceeded, sourceKind);
    }

    bool IPendingResourceOverwriteHost.IsPackageStillPending(ChartPackage package)
    {
        return IsPackageStillPending(package);
    }

    void IPendingResourceOverwriteHost.RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<ChartPackage> packages)
    {
        RemovePendingPackagesFromPendingListAndInstallRows(packages);
    }

    void IPendingResourceOverwriteHost.LogInfo(string info)
    {
        NLogWrapper.FileLogger?.Info(info);
    }
}
