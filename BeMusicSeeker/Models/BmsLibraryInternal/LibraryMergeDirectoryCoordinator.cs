using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryMergeDirectoryHost
{
    bool TryBlockLr2SongDbSyncMutation(string operation);

    void RunWithMergeDirectoryWriteLocks(long operationId, Action action);

    List<LibraryChartRef> CreateOwnedRealPathChartRefs(string directoryPath);

    InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot();

    LibraryMergeResult PrepareMergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<LibraryChartRef> sourceChartRefs,
        InstallDestinationOverlayChartRefSnapshot overlayChartRefs,
        long operationId);

    IReadOnlyDictionary<string, Lr2SongUserColumns> CreateSongUserColumnSnapshot(IEnumerable<string> paths);

    void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string performanceLogContext);

    DirectoryResourceLookupCache.ReverseLookupMutationResult RemoveReverseLookupDirectoriesUnderSource(string sourceDirectory, out int removedDirectoryCount);

    bool MoveChartPackageFiles(ChartPackage package, string destinationDirectory, IPrimaryHashLookup existingHashes);

    void InvalidateInstalledDirectoryIndex();

    void ShowFolderMergeFailed(string sourceDirectory, string destinationDirectory);

    void LogInstallPerformance(string message);

    void LogInstallPerformanceWarn(string message);

    void LogReverseLookupMutationAndQueueWarmupIfNeeded(string reason, DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult);

    DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectories(ChartScanResult scan);

    void ApplySongUserColumns(BMSFile bmsFile, Lr2SongUserColumns userColumns);

    void UpsertMergedBmsFiles(IEnumerable<BMSFile> bmsFiles);

    void UpsertMergedBmsonSongs(IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs);

    ChartStorageTargetSet CreateOwnedStorageTargetsForSubtreeDirectory(string directoryPath);

    void SetMergeFolderMaintenanceInfo(IEnumerable<ChartFile> charts);

    void ApplyInstalledChartStorageTargets(ChartStorageTargetSet targets);
}

internal static class LibraryMergeDirectoryCoordinator
{
    internal static void MergeChartDirectory(
        ILibraryMergeDirectoryHost host,
        string src,
        string dst,
        long operationId)
    {
        if (src == null)
        {
            throw new ArgumentNullException(nameof(src));
        }
        if (dst == null)
        {
            throw new ArgumentNullException(nameof(dst));
        }
        if (host.TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.MergeChartDirectory)))
        {
            return;
        }
        var totalStopwatch = Stopwatch.StartNew();
        host.LogInstallPerformance("duplicate_merge_model start op=" + operationId + " src=" + src + " dst=" + dst);
        try
        {
            host.RunWithMergeDirectoryWriteLocks(operationId, () =>
            {
                var prepareStopwatch = Stopwatch.StartNew();
                var sourceRefsStopwatch = Stopwatch.StartNew();
                List<LibraryChartRef> sourceChartRefs = host.CreateOwnedRealPathChartRefs(src);
                host.LogInstallPerformance("duplicate_merge_model prepare_source_refs_done op=" + operationId
                    + " elapsedMs=" + sourceRefsStopwatch.ElapsedMilliseconds
                    + " count=" + sourceChartRefs.Count);
                var overlayStopwatch = Stopwatch.StartNew();
                InstallDestinationOverlayChartRefSnapshot overlayChartRefs = host.CreateInstallDestinationOverlayChartRefSnapshot();
                host.LogInstallPerformance("duplicate_merge_model prepare_overlay_refs_done op=" + operationId
                    + " elapsedMs=" + overlayStopwatch.ElapsedMilliseconds
                    + " count=" + (overlayChartRefs?.ChartCount ?? 0));
                var prepareCoreStopwatch = Stopwatch.StartNew();
                LibraryMergeResult mergeResult = host.PrepareMergeDirectory(
                    src,
                    dst,
                    sourceChartRefs,
                    overlayChartRefs,
                    operationId);
                host.LogInstallPerformance("duplicate_merge_model prepare_core_done op=" + operationId
                    + " elapsedMs=" + prepareCoreStopwatch.ElapsedMilliseconds);
                int repackageEntryCount = mergeResult.Repackage?.ChartEntries?.Count ?? 0;
                host.LogInstallPerformance("duplicate_merge_model prepare_done op=" + operationId
                    + " success=" + mergeResult.Success
                    + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds
                    + " sourceCharts=" + mergeResult.SourceCharts.Count
                    + " repackageEntries=" + repackageEntryCount
                    + " existingHashes=" + (mergeResult.ExistingHashes?.DistinctPrimaryHashCount ?? 0)
                    + " installDestinations=" + mergeResult.ReferenceMutationDelta.UpdatedInstallDestinations.Count
                    + " installedPackagePaths=" + mergeResult.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count);
                if (!mergeResult.Success)
                {
                    host.LogInstallPerformance("duplicate_merge_model skipped op=" + operationId + " reason=no_source_charts totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    return;
                }

                var sourceSnapshotStopwatch = Stopwatch.StartNew();
                List<BMSFile> sourceBmsFiles = [.. mergeResult.SourceCharts
                    .Select(chart => chart?.GetBmsStorageOwner())
                    .Where(ChartFileKindResolver.IsBmsChartFile)];
                List<LR2SongDBExtended.bmson_song> sourceBmsonSongs = [.. mergeResult.SourceCharts
                    .Select(chart => chart?.GetBmsonStorageOwner())
                    .Where(song => song != null)
                    .Distinct()];
                IReadOnlyDictionary<string, Lr2SongUserColumns> sourceUserColumnsByPath = host.CreateSongUserColumnSnapshot(sourceBmsFiles.Select(file => file?.path));
                var sourceUserColumnsByOwner = new Dictionary<BMSFile, Lr2SongUserColumns>();
                foreach (BMSFile sourceBmsFile in sourceBmsFiles)
                {
                    if (sourceBmsFile != null
                        && !string.IsNullOrWhiteSpace(sourceBmsFile.path)
                        && sourceUserColumnsByPath.TryGetValue(sourceBmsFile.path, out Lr2SongUserColumns userColumns))
                    {
                        sourceUserColumnsByOwner[sourceBmsFile] = userColumns;
                    }
                }
                host.LogInstallPerformance("duplicate_merge_model source_snapshot_done op=" + operationId
                    + " elapsedMs=" + sourceSnapshotStopwatch.ElapsedMilliseconds
                    + " bms=" + sourceBmsFiles.Count
                    + " bmson=" + sourceBmsonSongs.Count);
                var unregisterStopwatch = Stopwatch.StartNew();
                host.ApplyLibraryMutationDeltaWithPerformanceContext(
                    BuildLibrarySourceUnregisterMutationDelta(sourceBmsFiles, sourceBmsonSongs),
                    "duplicate_merge_unregister op=" + operationId);
                host.LogInstallPerformance("duplicate_merge_model unregister_source_done op=" + operationId
                    + " elapsedMs=" + unregisterStopwatch.ElapsedMilliseconds
                    + " bms=" + sourceBmsFiles.Count
                    + " bmson=" + sourceBmsonSongs.Count);
                var reverseLookupRemoveStopwatch = Stopwatch.StartNew();
                DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = host.RemoveReverseLookupDirectoriesUnderSource(src, out int reverseLookupRemovedDirCount);
                host.LogInstallPerformance("duplicate_merge_model reverse_lookup_remove_done op=" + operationId + " elapsedMs=" + reverseLookupRemoveStopwatch.ElapsedMilliseconds + " dirs=" + reverseLookupRemovedDirCount);
                var moveStopwatch = Stopwatch.StartNew();
                host.LogInstallPerformance("duplicate_merge_model move_files_start op=" + operationId + " entries=" + repackageEntryCount + " src=" + src + " dst=" + dst);
                if (!host.MoveChartPackageFiles(mergeResult.Repackage, dst, mergeResult.ExistingHashes))
                {
                    host.InvalidateInstalledDirectoryIndex();
                    host.LogInstallPerformance("duplicate_merge_model move_files_failed op=" + operationId + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    host.ShowFolderMergeFailed(src, dst);
                    return;
                }
                host.LogInstallPerformance("duplicate_merge_model move_files_done op=" + operationId + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds);
                var scanStopwatch = Stopwatch.StartNew();
                if (!ChartDirectoryScanBuilder.TryBuildFromRoots([dst], out ChartScanResult mergedDirectoryScan, out string scanFailureReason))
                {
                    host.LogInstallPerformanceWarn("duplicate_merge_model dst_scan_skipped op=" + operationId + " reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown") + " elapsedMs=" + scanStopwatch.ElapsedMilliseconds);
                    mergedDirectoryScan = null;
                }
                else
                {
                    host.LogInstallPerformance("duplicate_merge_model dst_scan_done op=" + operationId + " elapsedMs=" + scanStopwatch.ElapsedMilliseconds + " chartDirs=" + mergedDirectoryScan.ChartDirectories.Count);
                }
                var reverseLookupAddStopwatch = Stopwatch.StartNew();
                reverseLookupMutation = reverseLookupMutation.Combine(host.AddReverseLookupDirectories(mergedDirectoryScan));
                host.LogInstallPerformance("duplicate_merge_model reverse_lookup_add_done op=" + operationId + " elapsedMs=" + reverseLookupAddStopwatch.ElapsedMilliseconds + " dirs=" + (mergedDirectoryScan?.ChartDirectories.Count ?? 0));
                host.LogReverseLookupMutationAndQueueWarmupIfNeeded("merge_folder", reverseLookupMutation);
                var applyDeltaStopwatch = Stopwatch.StartNew();
                host.ApplyLibraryMutationDeltaWithPerformanceContext(
                    mergeResult.ReferenceMutationDelta,
                    "duplicate_merge_reference_delta op=" + operationId);
                host.LogInstallPerformance("duplicate_merge_model apply_delta_done op=" + operationId
                    + " elapsedMs=" + applyDeltaStopwatch.ElapsedMilliseconds
                    + " installDestinations=" + mergeResult.ReferenceMutationDelta.UpdatedInstallDestinations.Count
                    + " installedPackagePaths=" + mergeResult.ReferenceMutationDelta.UpdatedInstalledPackagePaths.Count);
                var movedSnapshotStopwatch = Stopwatch.StartNew();
                List<PackageChartEntry> movedPackageEntries = mergeResult.Repackage.ChartEntries;
                ChartStorageTargetSet movedTargets = ChartStorageTargetSet.FromCharts(movedPackageEntries
                    .Select(entry => entry?.Chart)
                    .Where(chart => chart != null && IsFilePathUnderDirectory(chart.Path, dst)));
                List<BMSFile> movedBmsFiles = movedTargets.BmsFiles;
                List<LR2SongDBExtended.bmson_song> movedBmsonSongs = movedTargets.BmsonSongs;
                foreach (BMSFile movedBmsFile in movedBmsFiles)
                {
                    if (movedBmsFile != null && sourceUserColumnsByOwner.TryGetValue(movedBmsFile, out Lr2SongUserColumns userColumns))
                    {
                        host.ApplySongUserColumns(movedBmsFile, userColumns);
                    }
                }
                host.LogInstallPerformance("duplicate_merge_model moved_snapshot_done op=" + operationId
                    + " elapsedMs=" + movedSnapshotStopwatch.ElapsedMilliseconds
                    + " bms=" + movedBmsFiles.Count
                    + " bmson=" + movedBmsonSongs.Count);
                var dbStopwatch = Stopwatch.StartNew();
                host.UpsertMergedBmsFiles(movedBmsFiles);
                if (movedBmsonSongs.Count > 0)
                {
                    host.UpsertMergedBmsonSongs(movedBmsonSongs);
                }
                host.LogInstallPerformance("duplicate_merge_model db_upsert_done op=" + operationId + " elapsedMs=" + dbStopwatch.ElapsedMilliseconds + " bms=" + movedBmsFiles.Count + " bmson=" + movedBmsonSongs.Count);
                var maintenanceTargetStopwatch = Stopwatch.StartNew();
                ChartStorageTargetSet destinationMaintenanceTargets = host.CreateOwnedStorageTargetsForSubtreeDirectory(dst);
                ChartStorageTargetSet maintenanceTargets = ChartStorageTargetSet.FromCharts(destinationMaintenanceTargets.Charts.Concat(movedTargets.Charts));
                host.LogInstallPerformance("duplicate_merge_model maintenance_targets_done op=" + operationId
                    + " elapsedMs=" + maintenanceTargetStopwatch.ElapsedMilliseconds
                    + " bms=" + maintenanceTargets.BmsFiles.Count
                    + " bmson=" + maintenanceTargets.BmsonSongs.Count
                    + " destinationBmson=" + destinationMaintenanceTargets.BmsonSongs.Count);
                var maintenanceStopwatch = Stopwatch.StartNew();
                host.SetMergeFolderMaintenanceInfo(maintenanceTargets.Charts);
                host.LogInstallPerformance("duplicate_merge_model maintenance_done op=" + operationId + " elapsedMs=" + maintenanceStopwatch.ElapsedMilliseconds);
                var upsertStopwatch = Stopwatch.StartNew();
                host.ApplyInstalledChartStorageTargets(movedTargets);
                host.LogInstallPerformance("duplicate_merge_model upsert_library_done op=" + operationId
                    + " elapsedMs=" + upsertStopwatch.ElapsedMilliseconds
                    + " movedBms=" + movedBmsFiles.Count
                    + " movedBmson=" + movedBmsonSongs.Count
                    + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
            });
        }
        catch (Exception ex)
        {
            host.InvalidateInstalledDirectoryIndex();
            host.LogInstallPerformanceWarn("duplicate_merge_model failed op=" + operationId + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name);
            throw;
        }
    }

    private static LibraryMutationDelta BuildLibrarySourceUnregisterMutationDelta(
        IEnumerable<BMSFile> sourceBmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> sourceBmsonSongs)
    {
        var delta = new LibraryMutationDelta();
        delta.ChartRemoveRequests.AddRange((sourceBmsFiles ?? [])
            .Select(OwnedChartRemoveRequest.FromOwnerReference)
            .Where(request => request != null));
        delta.ChartRemoveRequests.AddRange((sourceBmsonSongs ?? [])
            .Select(OwnedChartRemoveRequest.FromOwnerReference)
            .Where(request => request != null));
        bool hasCharts = delta.ChartRemoveRequests.Count > 0;
        delta.InvalidateInstalledDirectoryIndex = hasCharts;
        delta.InvalidateParentFolderCache = hasCharts;
        delta.ClearDuplicatedCache = hasCharts;
        return delta;
    }

    private static bool IsFilePathUnderDirectory(string filePath, string directoryPath)
    {
        return !string.IsNullOrWhiteSpace(filePath)
            && !string.IsNullOrWhiteSpace(directoryPath)
            && filePath.StartsWith(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
