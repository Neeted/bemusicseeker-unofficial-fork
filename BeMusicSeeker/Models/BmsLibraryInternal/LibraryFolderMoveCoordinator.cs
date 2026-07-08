using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    List<LibraryChartRef> CreateNonNullChartRefList(IEnumerable<LibraryChartRef> charts);

    List<FolderAutoRenamePlan> BuildRootFolderMovePlans(List<LibraryChartRef> charts, string dstDir);

    DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(string srcDir, string dstDir);

    LibraryMutationDelta BuildFolderMoveDelta(string srcDir, string dstDir, bool unregister, bool notifyStorageRowPathChanges);

    void ApplyLibraryMutationDelta(LibraryMutationDelta delta);

    void InvalidateDuplicateChartGroupsCache();

    void LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult);

    void ShowCannotRenameRootFolder(string srcDir);

    void ShowRenameFolderNotExists(string srcDir);

    void ShowMoveDestinationAlreadyExists(string srcDir, string dstDir);

    void ShowMoveDestinationRootNotFound(string dstDir);

    void ShowDriveRootCannotChangeRoot();

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

    internal static void MoveLibraryRootFolder(
        ILibraryFolderMoveHost host,
        IEnumerable<LibraryChartRef> charts,
        string dstDir,
        bool? unregister)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (dstDir == null)
        {
            throw new ArgumentNullException(nameof(dstDir));
        }
        if (host.TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.MoveLibraryRootFolder)))
        {
            return;
        }
        host.RunWithFolderMoveWriteLocks(() =>
        {
            if (!host.DirectoryExists(dstDir))
            {
                host.ShowMoveDestinationRootNotFound(dstDir);
                return;
            }
            List<LibraryChartRef> chartList = host.CreateNonNullChartRefList(charts);
            List<FolderAutoRenamePlan> plans = host.BuildRootFolderMovePlans(chartList, dstDir);
            if (ContainsDriveRootSource(chartList))
            {
                host.ShowDriveRootCannotChangeRoot();
            }
            foreach (FolderAutoRenamePlan plan in plans)
            {
                MoveLibraryChartFolder(
                    host,
                    plan.SourceDirectory,
                    plan.DestinationDirectory,
                    unregister,
                    notifyStorageRowPathChanges: true);
            }
        });
    }

    private static bool ContainsDriveRootSource(IEnumerable<LibraryChartRef> charts)
    {
        return (charts ?? [])
            .Select(chart => DirectoryExt.GetDirectoryNameSimple(chart.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(f => !string.IsNullOrWhiteSpace(f) && Path.GetPathRoot(f).Equals(f, StringComparison.OrdinalIgnoreCase));
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
