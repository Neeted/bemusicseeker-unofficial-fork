using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Livet;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IForceInstallPendingPackagesHost
{
    bool IForceInstallPendingPackagesHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    IDisposable IForceInstallPendingPackagesHost.AcquireBmsFilesInitializedAllReaderGuard()
    {
        return rwlockBMSFilesInitializedAll.GetReaderGuard();
    }

    IDisposable IForceInstallPendingPackagesHost.AcquirePendingInstallChartsWriterGuard()
    {
        return rwlockPendingInstallCharts.GetWriterGuard();
    }

    IDisposable IForceInstallPendingPackagesHost.AcquireBmsFilesWriterGuard()
    {
        return rwlockBMSFiles.GetWriterGuard();
    }

    IDisposable IForceInstallPendingPackagesHost.AcquireSongDbInstallWriterGuard()
    {
        return rwlockSongDBInstall.GetWriterGuard();
    }

    bool IForceInstallPendingPackagesHost.HasBmsFiles
    {
        get
        {
            return BMSFiles != null;
        }
    }

    IEnumerable<ChartPackage> IForceInstallPendingPackagesHost.PendingPackages
    {
        get
        {
            return ChartPackagesPending;
        }
    }

    IEnumerable<ChartPackage> IForceInstallPendingPackagesHost.InstalledPackages
    {
        get
        {
            return ChartPackagesInstalled;
        }
    }

    bool IForceInstallPendingPackagesHost.ConfirmNormalInstallOverride()
    {
        return ShowOperationDialog(Resources.Confirm_NormalInstallOverride, Resources.Confirm_NormalInstallTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    List<ChartPackage> IForceInstallPendingPackagesHost.InstallChartPackagesForForceInstall(IEnumerable<ChartPackage> packagesToInstall, List<ChartPackage> deferredInstalledPackages)
    {
        return installChartPackages(packagesToInstall, null, null, deferredInstalledPackages);
    }

    void IForceInstallPendingPackagesHost.ApplyPendingPackageRemovals(IEnumerable<ChartPackage> packagesToRemove)
    {
        packageLifecycleOwner.ApplyPendingPackageMutationDelta(BuildPendingPackageMutationDelta(packagesToRemove: packagesToRemove));
    }

    void IForceInstallPendingPackagesHost.ReplaceInstalledPackages(IEnumerable<ChartPackage> packages)
    {
        packageLifecycleOwner.ReplaceInstalledPackages(packages);
    }

    void IForceInstallPendingPackagesHost.LogInfo(string info)
    {
        NLogWrapper.FileLogger?.Info(info);
    }
}
