using System;
using System.IO;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryFolderMoveHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    bool IsLibraryRootFolder(string folderPath);

    string NormalizeAutoRenameFolderName(string folderName);

    bool DirectoryExists(string folderPath);

    bool EntryExists(string path);

    void RunWithFolderMoveWriteLocks(Action action);

    DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(string srcDir, string dstDir);

    LibraryMutationDelta BuildFolderMoveDelta(string srcDir, string dstDir, bool unregister, bool notifyStorageRowPathChanges);

    void ApplyLibraryMutationDelta(LibraryMutationDelta delta);

    void InvalidateDuplicateChartGroupsCache();

    void LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult);

    void ShowCannotRenameRootFolder(string srcDir);

    void ShowRenameFolderNotExists(string srcDir);

    void ShowMoveDestinationAlreadyExists(string srcDir, string dstDir);

    void ShowFolderMoveFailed(string srcDir, string dstDir, Exception exception);
}

internal static class LibraryFolderMoveCoordinator
{
    internal static void RenameChartFolder(
        ILibraryFolderMoveHost host,
        string srcDir,
        string newName,
        bool? unregister,
        bool renameRootFolder)
    {
        if (srcDir == null)
        {
            throw new ArgumentNullException(nameof(srcDir));
        }
        if (newName == null)
        {
            throw new ArgumentNullException(nameof(newName));
        }
        if (host.TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.RenameChartFolder)))
        {
            return;
        }
        if (!renameRootFolder && host.IsLibraryRootFolder(srcDir))
        {
            host.ShowCannotRenameRootFolder(srcDir);
            return;
        }
        newName = host.NormalizeAutoRenameFolderName(newName);
        if (string.IsNullOrWhiteSpace(newName) || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!host.DirectoryExists(srcDir))
        {
            host.ShowRenameFolderNotExists(srcDir);
            return;
        }
        host.RunWithFolderMoveWriteLocks(() =>
        {
            string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
            MoveLibraryChartFolder(
                host,
                srcDir,
                dstDir,
                unregister,
                notifyStorageRowPathChanges: false);
            if (unregister == false)
            {
                host.InvalidateDuplicateChartGroupsCache();
            }
        });
    }

    internal static void MoveLibraryChartFolder(
        ILibraryFolderMoveHost host,
        string srcDir,
        string dstDir,
        bool? unregister,
        bool notifyStorageRowPathChanges)
    {
        if (!TryMoveLibraryChartFolder(host, srcDir, dstDir))
        {
            return;
        }
        if (unregister != false && unregister != true)
        {
            return;
        }
        LibraryMutationDelta delta = host.BuildFolderMoveDelta(
            srcDir,
            dstDir,
            unregister == true,
            notifyStorageRowPathChanges);
        host.ApplyLibraryMutationDelta(delta);
    }

    private static bool TryMoveLibraryChartFolder(ILibraryFolderMoveHost host, string srcDir, string dstDir)
    {
        if (srcDir.Equals(dstDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (host.EntryExists(dstDir))
        {
            host.ShowMoveDestinationAlreadyExists(srcDir, dstDir);
            return false;
        }
        try
        {
            DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = host.MoveFolderAndUpdateReferences(srcDir, dstDir);
            host.LogReverseLookupMutationAndQueueWarmupIfNeeded("move_folder", reverseLookupMutation);
            return true;
        }
        catch (Exception moveException)
        {
            host.ShowFolderMoveFailed(srcDir, dstDir, moveException);
            return false;
        }
    }
}
