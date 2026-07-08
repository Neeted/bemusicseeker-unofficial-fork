using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : IInvalidExtensionRenameHost
{
    bool IInvalidExtensionRenameHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    void IInvalidExtensionRenameHost.RunWithNormalInvalidExtensionRenameWriteLocks(Action action)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockBMSFiles.GetWriterGuard())
            {
                action();
            }
        }
    }

    void IInvalidExtensionRenameHost.RunWithPendingInvalidExtensionRenameWriteLocks(Action action)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockSongDBInstall.GetWriterGuard())
                {
                    action();
                }
            }
        }
    }

    LibraryMutationDelta IInvalidExtensionRenameHost.RenameLibraryFileExtensions(
        IEnumerable<ChartFile> targetCharts,
        string newExt,
        bool unregister)
    {
        return libraryFileOperationsService.RenameLibraryFileExtensions(
            targetCharts,
            newExt,
            unregister,
            (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, unregister));
    }

    PendingExtensionRenameResult IInvalidExtensionRenameHost.RenamePendingBmsFormatChartFileExtensions(
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        return packageInstallService.RenamePendingBmsFormatChartFileExtensions(
            charts,
            newExt,
            (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false));
    }

    void IInvalidExtensionRenameHost.ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        ApplyLibraryMutationDelta(delta);
    }

    void IInvalidExtensionRenameHost.RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
    {
        RemovePendingChartsFromPendingPackagesAndInstallRows(chartPaths);
    }

    void IInvalidExtensionRenameHost.ShowNormalRenameFailure(LibraryDeleteFailure failure, string newExt)
    {
        if (failure.Exception == null)
        {
            return;
        }
        ShowOperationDialog(
            string.Format(Resources.Error_BmsFileMoveFailed, failure.Path, newExt, GetDisplayedExceptionMessage(failure.Exception)),
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    void IInvalidExtensionRenameHost.ShowPendingRenameFailure(PendingExtensionRenameFailure failure)
    {
        if (failure?.Outcome?.FailureException == null || failure.File == null)
        {
            return;
        }
        if (failure.Outcome.FailedDuringDelete)
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.File.path, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileMoveFailed, failure.File.path, failure.Outcome.FinalPath, GetDisplayedExceptionMessage(failure.Outcome.FailureException)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
    }

    void IInvalidExtensionRenameHost.LogInfo(string info)
    {
        NLogWrapper.FileLogger?.Info(info);
    }
}
