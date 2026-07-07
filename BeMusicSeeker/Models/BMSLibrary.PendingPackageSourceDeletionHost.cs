using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IPendingPackageSourceDeletionHost
{
    IEnumerable<ChartPackage> IPendingPackageSourceDeletionHost.PendingPackages
    {
        get
        {
            return ChartPackagesPending;
        }
    }

    IFileMutationService IPendingPackageSourceDeletionHost.FileMutationService
    {
        get
        {
            return fileMutationService;
        }
    }

    FileMutationOptions IPendingPackageSourceDeletionHost.TargetOnlyFileMutationOptions
    {
        get
        {
            return targetOnlyFileMutationOptions;
        }
    }

    FileMutationOptions IPendingPackageSourceDeletionHost.RecursiveDirectoryTreeFileMutationOptions
    {
        get
        {
            return recursiveDirectoryTreeFileMutationOptions;
        }
    }

    IDisposable IPendingPackageSourceDeletionHost.AcquireBmsFilesInitializedAllReaderGuard()
    {
        return rwlockBMSFilesInitializedAll.GetReaderGuard();
    }

    IDisposable IPendingPackageSourceDeletionHost.AcquirePendingInstallChartsWriterGuard()
    {
        return rwlockPendingInstallCharts.GetWriterGuard();
    }

    IDisposable IPendingPackageSourceDeletionHost.AcquireSongDbInstallWriterGuard()
    {
        return rwlockSongDBInstall.GetWriterGuard();
    }

    void IPendingPackageSourceDeletionHost.RemovePendingPackagesFromPendingListAndInstallRows(IEnumerable<ChartPackage> packages)
    {
        RemovePendingPackagesFromPendingListAndInstallRows(packages);
    }

    void IPendingPackageSourceDeletionHost.ShowSourceDeletionFailure(PendingPackageSourceDeletionFailure failure)
    {
        if (failure?.Package == null)
        {
            return;
        }
        NLogWrapper.FileLogger?.Warn(failure.Exception, "advanced_pending_cleanup failed path=" + failure.Package.path + " kind=" + (failure.IsDirectory ? "directory" : "file") + " error=" + GetDisplayedExceptionMessage(failure.Exception));
        if (failure.IsDirectory)
        {
            ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Package.path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
    }

    void IPendingPackageSourceDeletionHost.LogInfo(string info)
    {
        NLogWrapper.FileLogger?.Info(info);
    }
}
