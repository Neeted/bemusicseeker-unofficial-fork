using System;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : ILibraryFolderMoveHost
{
    bool ILibraryFolderMoveHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    bool ILibraryFolderMoveHost.IsLibraryRootFolder(string folderPath)
    {
        return getBMSDirectories().Contains(folderPath, StringComparer.OrdinalIgnoreCase);
    }

    string ILibraryFolderMoveHost.NormalizeAutoRenameFolderName(string folderName)
    {
        return NormalizeAutoRenameFolderName(folderName);
    }

    bool ILibraryFolderMoveHost.DirectoryExists(string folderPath)
    {
        return LongPathFileSystem.DirectoryExists(folderPath);
    }

    bool ILibraryFolderMoveHost.EntryExists(string path)
    {
        return LongPathFileSystem.EntryExists(path);
    }

    void ILibraryFolderMoveHost.RunWithFolderMoveWriteLocks(Action action)
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

    DirectoryResourceLookupCache.ReverseLookupMutationResult ILibraryFolderMoveHost.MoveFolderAndUpdateReferences(string srcDir, string dstDir)
    {
        return libraryFileOperationsService.MoveFolderAndUpdateReferences(
            srcDir,
            dstDir,
            directoryResourceLookupCache,
            fileMutationService,
            recursiveDirectoryTreeFileMutationOptions);
    }

    LibraryMutationDelta ILibraryFolderMoveHost.BuildFolderMoveDelta(
        string srcDir,
        string dstDir,
        bool unregister,
        bool notifyStorageRowPathChanges)
    {
        return libraryFileOperationsService.BuildFolderMoveDelta(
            srcDir,
            dstDir,
            CreateOwnedRealPathChartRefsUnsafe(srcDir),
            CreateInstallDestinationOverlayChartRefSnapshotUnsafe(),
            ChartPackagesPending,
            ChartPackagesInstalled,
            unregister,
            notifyStorageRowPathChanges);
    }

    void ILibraryFolderMoveHost.ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        ApplyLibraryMutationDelta(delta);
    }

    void ILibraryFolderMoveHost.InvalidateDuplicateChartGroupsCache()
    {
        InvalidateDuplicateChartGroupsCache();
    }

    void ILibraryFolderMoveHost.LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
    {
        LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
    }

    void ILibraryFolderMoveHost.ShowCannotRenameRootFolder(string srcDir)
    {
        ShowOperationDialog(string.Format(Resources.Warn_CannotRenameRootFolder, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
    }

    void ILibraryFolderMoveHost.ShowRenameFolderNotExists(string srcDir)
    {
        ShowOperationDialog(string.Format(Resources.Warn_RenameFolderNotExists, srcDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
    }

    void ILibraryFolderMoveHost.ShowMoveDestinationAlreadyExists(string srcDir, string dstDir)
    {
        ShowOperationDialog(string.Format(Resources.Warn_MoveDestAlreadyExists, srcDir, dstDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
    }

    void ILibraryFolderMoveHost.ShowFolderMoveFailed(string srcDir, string dstDir, Exception exception)
    {
        ShowOperationDialog(string.Format(Resources.Error_FolderMoveFailed, srcDir, dstDir, GetDisplayedExceptionMessage(exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
    }
}
