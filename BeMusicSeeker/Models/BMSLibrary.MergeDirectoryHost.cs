using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary : ILibraryMergeDirectoryHost
{
    bool ILibraryMergeDirectoryHost.TryBlockLr2SongDbSyncMutation(string operation)
    {
        return TryBlockLr2SongDbSyncMutation(operation);
    }

    void ILibraryMergeDirectoryHost.RunWithMergeDirectoryWriteLocks(long operationId, Action action)
    {
        var initializedLockWaitStopwatch = Stopwatch.StartNew();
        using (rwlockBMSFilesInitializedMin.GetReaderGuard())
        {
            LogInstallPerformance("duplicate_merge_model initialized_lock_acquired op=" + operationId + " waitMs=" + initializedLockWaitStopwatch.ElapsedMilliseconds);
            var pendingLockWaitStopwatch = Stopwatch.StartNew();
            using (rwlockPendingInstallCharts.GetWriterGuard())
            {
                LogInstallPerformance("duplicate_merge_model pending_lock_acquired op=" + operationId + " waitMs=" + pendingLockWaitStopwatch.ElapsedMilliseconds);
                var bmsLockWaitStopwatch = Stopwatch.StartNew();
                using (rwlockBMSFiles.GetWriterGuard())
                {
                    LogInstallPerformance("duplicate_merge_model bms_lock_acquired op=" + operationId + " waitMs=" + bmsLockWaitStopwatch.ElapsedMilliseconds);
                    action();
                }
            }
        }
    }

    List<LibraryChartRef> ILibraryMergeDirectoryHost.CreateOwnedRealPathChartRefs(string directoryPath)
    {
        return CreateOwnedRealPathChartRefsUnsafe(directoryPath);
    }

    InstallDestinationOverlayChartRefSnapshot ILibraryMergeDirectoryHost.CreateInstallDestinationOverlayChartRefSnapshot()
    {
        return installDestinationStateOwner.CreateOverlaySnapshot(out _);
    }

    LibraryMergeResult ILibraryMergeDirectoryHost.PrepareMergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<LibraryChartRef> sourceChartRefs,
        InstallDestinationOverlayChartRefSnapshot overlayChartRefs,
        long operationId)
    {
        return libraryFileOperationsService.PrepareMergeDirectory(
            sourceDirectory,
            destinationDirectory,
            sourceChartRefs,
            overlayChartRefs,
            ChartPackagesPending,
            ChartPackagesInstalled,
            excluded => CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, "duplicate_merge_prepare", operationId));
    }

    IReadOnlyDictionary<string, Lr2SongUserColumns> ILibraryMergeDirectoryHost.CreateSongUserColumnSnapshot(IEnumerable<string> paths)
    {
        return dbGateway.CreateSongUserColumnSnapshot(paths);
    }

    void ILibraryMergeDirectoryHost.ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string performanceLogContext)
    {
        ApplyLibraryMutationDeltaWithPerformanceContext(delta, performanceLogContext);
    }

    DirectoryResourceLookupCache.ReverseLookupMutationResult ILibraryMergeDirectoryHost.RemoveReverseLookupDirectoriesUnderSource(string sourceDirectory, out int removedDirectoryCount)
    {
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        List<string> removedDirectories = [.. (directoryResourceLookupCache?.Keys ?? []).Where(f => (f + Path.DirectorySeparatorChar).StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
        foreach (string item in removedDirectories)
        {
            mutation = mutation.Combine(directoryResourceLookupCache.RemoveDirWithResult(item));
        }
        removedDirectoryCount = removedDirectories.Count;
        return mutation;
    }

    bool ILibraryMergeDirectoryHost.MoveChartPackageFiles(ChartPackage package, string destinationDirectory, IPrimaryHashLookup existingHashes)
    {
        return MoveChartPackageFiles(
            package,
            destinationDirectory,
            showMessageBoxOnInstallFail: false,
            deleteAllContents: true,
            existingHashes: existingHashes);
    }

    void ILibraryMergeDirectoryHost.InvalidateInstalledDirectoryIndex()
    {
        InvalidateInstalledDirectoryIndex();
    }

    void ILibraryMergeDirectoryHost.ShowFolderMergeFailed(string sourceDirectory, string destinationDirectory)
    {
        ShowOperationDialog(string.Format(Resources.Error_BmsFolderMergeFailed, sourceDirectory, destinationDirectory), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
    }

    void ILibraryMergeDirectoryHost.LogInstallPerformance(string message)
    {
        LogInstallPerformance(message);
    }

    void ILibraryMergeDirectoryHost.LogInstallPerformanceWarn(string message)
    {
        LogInstallPerformanceWarn(message);
    }

    void ILibraryMergeDirectoryHost.LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
    {
        LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
    }

    DirectoryResourceLookupCache.ReverseLookupMutationResult ILibraryMergeDirectoryHost.AddReverseLookupDirectories(ChartScanResult scan)
    {
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string chartDirectory in scan?.ChartDirectories ?? [])
        {
            mutation = mutation.Combine(directoryResourceLookupCache.AddDir(chartDirectory, scan));
        }
        return mutation;
    }

    void ILibraryMergeDirectoryHost.ApplySongUserColumns(BMSFile bmsFile, Lr2SongUserColumns userColumns)
    {
        BmsLibraryDbGateway.ApplySongUserColumns(bmsFile, userColumns);
    }

    void ILibraryMergeDirectoryHost.UpsertMergedBmsFiles(IEnumerable<BMSFile> bmsFiles)
    {
        ExecuteLr2SongDbWrite(
            () => dbGateway.UpsertSongs(bmsFiles),
            stage: "lr2_song_db_duplicate_merge_upsert_failed",
            logReason: "duplicate_merge");
    }

    void ILibraryMergeDirectoryHost.UpsertMergedBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        dbGateway.UpsertBmsonSongs(bmsonSongs);
    }

    ChartStorageTargetSet ILibraryMergeDirectoryHost.CreateOwnedStorageTargetsForSubtreeDirectory(string directoryPath)
    {
        return CreateOwnedStorageTargetsForSubtreeDirectoryUnsafe(directoryPath);
    }

    void ILibraryMergeDirectoryHost.ApplyMergeFolderMaintenance(IEnumerable<ChartFile> charts)
    {
        // Merge finalizes BMSFiles/BmsonSongs after maintenance. Building the full warning index here
        // would immediately be invalidated by that final library replacement, so defer it to the next view
        // that actually needs the resource-health projection.
        ApplyCatalogMaintenance(
            charts,
            forceUpdate: true,
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            resourceHealthMutationReason: "merge_folder");
    }

    void ILibraryMergeDirectoryHost.ApplyInstalledChartStorageTargets(ChartStorageTargetSet targets)
    {
        ApplyInstalledChartStorageTargets(targets, "merge_folder");
    }
}
