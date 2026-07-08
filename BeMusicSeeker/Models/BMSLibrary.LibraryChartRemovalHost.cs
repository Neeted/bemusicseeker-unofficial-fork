using System;
using System.Collections.Generic;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : ILibraryChartRemovalHost
{
    bool ILibraryChartRemovalHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    void ILibraryChartRemovalHost.RunWithLibraryChartRemovalWriteLocks(Action action)
    {
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    action();
                }
            }
        }
    }

    LibraryRemovalResult ILibraryChartRemovalHost.DeleteLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        Func<string, bool> confirmDeleteWholeFolder)
    {
        return libraryFileOperationsService.DeleteLibraryCharts(
            charts,
            CreateOwnedCanonicalChartLookupUnsafe(),
            CreateInstallDestinationOverlayChartRefSnapshotUnsafe(),
            ChartPackagesPending,
            directoryResourceLookupCache,
            sendToRecycleBin,
            confirmDeleteWholeFolder,
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions);
    }

    void ILibraryChartRemovalHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }

    void ILibraryChartRemovalHost.LogReverseLookupMutationAndQueueWarmupIfNeeded(
        string reason,
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
    {
        LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
    }

    void ILibraryChartRemovalHost.ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        ApplyLibraryMutationDelta(delta);
    }

    bool ILibraryChartRemovalHost.ConfirmDeleteWholeFolder(string folderPath)
    {
        return ShowOperationDialog(string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderPath), Resources.MessageBoxTitle_Confirm, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    void ILibraryChartRemovalHost.ShowDeleteFailure(LibraryDeleteFailure failure)
    {
        if (failure.IsDirectory)
        {
            ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
    }
}
