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
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    internal sealed partial class LibraryFileOperationOwner
    {
        internal void MergeChartDirectory(string sourceDirectory, string destinationDirectory, long operationId)
        {
            if (sourceDirectory == null)
            {
                throw new ArgumentNullException(nameof(sourceDirectory));
            }
            if (destinationDirectory == null)
            {
                throw new ArgumentNullException(nameof(destinationDirectory));
            }
            if (TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.MergeChartDirectory)))
            {
                return;
            }

            Stopwatch totalStopwatch = Stopwatch.StartNew();
            BMSLibrary.LogInstallPerformance("duplicate_merge_model start op=" + operationId + " src=" + sourceDirectory + " dst=" + destinationDirectory);
            try
            {
                RunWithMergeDirectoryWriteLocks(operationId, () =>
                {
                    List<LibraryChartRef> sourceChartRefs = owner.CreateOwnedRealPathChartRefsUnsafe(sourceDirectory);
                    InstallDestinationOverlayChartRefSnapshot overlayChartRefs = owner.installDestinationStateOwner.CreateOverlaySnapshot(out _);
                    LibraryMergeResult mergeResult = owner.libraryFileOperationsService.PrepareMergeDirectory(
                        sourceDirectory,
                        destinationDirectory,
                        sourceChartRefs,
                        overlayChartRefs,
                        owner.ChartPackagesPending,
                        owner.ChartPackagesInstalled,
                        excluded => owner.CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, "duplicate_merge_prepare", operationId));
                    if (!mergeResult.Success)
                    {
                        BMSLibrary.LogInstallPerformance("duplicate_merge_model skipped op=" + operationId + " reason=no_source_charts totalMs=" + totalStopwatch.ElapsedMilliseconds);
                        return;
                    }

                    List<BMSFile> sourceBmsFiles = [.. mergeResult.SourceCharts
                        .Select(chart => chart?.GetChartSnapshot()?.GetBmsStorageOwner())
                        .Where(ChartFileKindResolver.IsBmsChartFile)];
                    List<LR2SongDBExtended.bmson_song> sourceBmsonSongs = [.. mergeResult.SourceCharts
                        .Select(chart => chart?.GetChartSnapshot()?.GetBmsonStorageOwner())
                        .Where(song => song != null)
                        .Distinct()];
                    HashSet<string> sourceBmsPaths = new(
                        sourceBmsFiles.Select(file => file?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);
                    HashSet<string> sourceBmsonPaths = new(
                        sourceBmsonSongs.Select(song => song?.path).Where(path => !string.IsNullOrWhiteSpace(path)),
                        StringComparer.OrdinalIgnoreCase);

                    DetachedMergePackage detachedPackage = CreateDetachedMergePackage(mergeResult.SourceCharts, sourceDirectory);
                    mergeResult.Repackage = detachedPackage.Package;

                    if (!MoveChartPackageFilesForMerge(mergeResult.Repackage, destinationDirectory, mergeResult.ExistingHashes))
                    {
                        owner.InvalidateInstalledDirectoryIndex();
                        ShowFolderMergeFailed(sourceDirectory, destinationDirectory);
                        return;
                    }

                    ChartScanResult mergedDirectoryScan = null;
                    if (ChartDirectoryScanBuilder.TryBuildFromRoots([destinationDirectory], out ChartScanResult scan, out string scanFailureReason))
                    {
                        mergedDirectoryScan = scan;
                    }
                    else
                    {
                        BMSLibrary.LogInstallPerformanceWarn("duplicate_merge_model dst_scan_skipped op=" + operationId + " reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown"));
                    }

                    ChartStorageTargetSet movedTargets = ChartStorageTargetSet.FromCharts(
                        mergeResult.Repackage.ChartEntries
                            .Select(entry => entry?.Chart)
                            .Where(chart => chart != null && IsFilePathUnderDirectory(chart.Path, destinationDirectory)));

                    LibraryMutationDelta catalogDelta = mergeResult.ReferenceMutationDelta;
                    HashSet<string> movedBmsSourcePaths = new(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> movedBmsonSourcePaths = new(StringComparer.OrdinalIgnoreCase);
                    foreach (BMSFile movedBmsFile in movedTargets.BmsFiles)
                    {
                        if (detachedPackage.CanonicalBmsByDetached.TryGetValue(movedBmsFile, out BMSFile canonicalBmsFile))
                        {
                            movedBmsSourcePaths.Add(canonicalBmsFile.path);
                            catalogDelta.ChartPathChanges.Add(new LibraryChartPathChange
                            {
                                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(canonicalBmsFile),
                                OldPath = canonicalBmsFile.path,
                                NewPath = movedBmsFile.path
                            });
                        }
                    }
                    foreach (LR2SongDBExtended.bmson_song movedBmsonSong in movedTargets.BmsonSongs)
                    {
                        if (detachedPackage.CanonicalBmsonByDetached.TryGetValue(movedBmsonSong, out LR2SongDBExtended.bmson_song canonicalBmsonSong))
                        {
                            movedBmsonSourcePaths.Add(canonicalBmsonSong.path);
                            catalogDelta.ChartPathChanges.Add(new LibraryChartPathChange
                            {
                                Chart = ChartFileProjection.FromBmsonStorageOwnerIdentity(canonicalBmsonSong),
                                OldPath = canonicalBmsonSong.path,
                                NewPath = movedBmsonSong.path
                            });
                        }
                    }
                    foreach (string sourcePath in sourceBmsPaths.Where(path => !movedBmsSourcePaths.Contains(path)))
                    {
                        catalogDelta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, sourcePath));
                    }
                    foreach (string sourcePath in sourceBmsonPaths.Where(path => !movedBmsonSourcePaths.Contains(path)))
                    {
                        catalogDelta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, sourcePath));
                    }
                    catalogDelta.InvalidateInstalledDirectoryIndex = true;
                    catalogDelta.InvalidateParentFolderCache = true;
                    catalogDelta.ClearDuplicatedCache = true;
                    owner.ApplyLibraryMutationDeltaWithPerformanceContext(catalogDelta, "duplicate_merge_catalog_transition op=" + operationId);

                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation = RemoveReverseLookupDirectoriesUnderSource(sourceDirectory);
                    reverseLookupMutation = reverseLookupMutation.Combine(AddReverseLookupDirectories(mergedDirectoryScan));
                    owner.LogReverseLookupMutationAndQueueWarmupIfNeeded("merge_folder", reverseLookupMutation);

                    ChartStorageTargetSet destinationMaintenanceTargets = owner.CreateOwnedStorageTargetsForSubtreeDirectoryUnsafe(destinationDirectory);
                    ApplyMergeFolderMaintenance(destinationMaintenanceTargets.Charts);
                    BMSLibrary.LogInstallPerformance("duplicate_merge_model done op=" + operationId
                        + " movedBms=" + movedTargets.BmsFiles.Count
                        + " movedBmson=" + movedTargets.BmsonSongs.Count
                        + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                });
            }
            catch (Exception ex)
            {
                owner.InvalidateInstalledDirectoryIndex();
                BMSLibrary.LogInstallPerformanceWarn("duplicate_merge_model failed op=" + operationId + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name);
                throw;
            }
        }

        private static DetachedMergePackage CreateDetachedMergePackage(
            IEnumerable<LibraryChartRef> sourceCharts,
            string sourceDirectory)
        {
            var detachedBmsByCanonical = new Dictionary<BMSFile, BMSFile>();
            var detachedBmsonByCanonical = new Dictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song>();
            var canonicalBmsByDetached = new Dictionary<BMSFile, BMSFile>();
            var canonicalBmsonByDetached = new Dictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song>();
            List<PackageChartEntry> entries = [];
            foreach (LibraryChartRef sourceChart in sourceCharts ?? [])
            {
                ChartFile chartSnapshot = sourceChart?.GetChartSnapshot() ?? sourceChart?.ToChartFile();
                if (chartSnapshot == null)
                {
                    continue;
                }

                ChartFile detachedChart = chartSnapshot;
                BMSFile canonicalBmsFile = sourceChart.GetBmsStorageOwner();
                if (canonicalBmsFile != null)
                {
                    if (!detachedBmsByCanonical.TryGetValue(canonicalBmsFile, out BMSFile detachedBmsFile))
                    {
                        detachedBmsFile = canonicalBmsFile.CreateSongRowPersistenceCopy();
                        detachedBmsByCanonical[canonicalBmsFile] = detachedBmsFile;
                        canonicalBmsByDetached[detachedBmsFile] = canonicalBmsFile;
                    }
                    detachedChart = CreateDetachedChart(chartSnapshot, detachedBmsFile, null);
                }
                else
                {
                    LR2SongDBExtended.bmson_song canonicalBmsonSong = sourceChart.GetBmsonStorageOwner();
                    if (canonicalBmsonSong != null)
                    {
                        if (!detachedBmsonByCanonical.TryGetValue(canonicalBmsonSong, out LR2SongDBExtended.bmson_song detachedBmsonSong))
                        {
                            detachedBmsonSong = CreateBmsonPersistenceCopy(canonicalBmsonSong);
                            detachedBmsonByCanonical[canonicalBmsonSong] = detachedBmsonSong;
                            canonicalBmsonByDetached[detachedBmsonSong] = canonicalBmsonSong;
                        }
                        detachedChart = CreateDetachedChart(chartSnapshot, null, detachedBmsonSong);
                    }
                }

                PackageChartEntry entry = PackageChartEntry.FromChart(detachedChart);
                if (entry != null)
                {
                    entries.Add(entry);
                }
            }

            ChartPackage package = ChartPackage.FromChartEntries(entries);
            package.path = sourceDirectory;
            package.delete_parent = false;
            return new DetachedMergePackage(
                package,
                canonicalBmsByDetached,
                canonicalBmsonByDetached);
        }

        private static ChartFile CreateDetachedChart(
            ChartFile source,
            BMSFile bmsFile,
            LR2SongDBExtended.bmson_song bmsonSong)
        {
            return new ChartFile(
                source.Kind,
                source.Path,
                source.Md5,
                source.Sha256,
                source.Title,
                source.RawTitle,
                source.Artist,
                source.Genre,
                source.Folder,
                source.Tag,
                source.LevelText,
                source.Level,
                source.Mode,
                source.ChartInfo,
                bmsFile,
                bmsonSong,
                source.Subtitle,
                source.AudioResourcePaths,
                source.VisualResourcePaths,
                source.Stagefile,
                source.Backbmp,
                source.Banner,
                source.InstallDestination,
                source.InstallDestinationTitle,
                source.InstallDestinationArtist,
                source.InstallDestinationSuggestions,
                source.Warnings,
                source.WAVHealth,
                source.BGAHealth,
                source.MovieHealth,
                source.StagefileHealth,
                source.BannerHealth,
                source.BackbmpHealth,
                source.EncodingName,
                source.Score,
                source.Status,
                source.ResourceHealthWarningsIgnored,
                source.ResourceHealthMaintenanceSnapshot);
        }

        private static LR2SongDBExtended.bmson_song CreateBmsonPersistenceCopy(
            LR2SongDBExtended.bmson_song source)
        {
            return new LR2SongDBExtended.bmson_song
            {
                path = source.path,
                folder = source.folder,
                title = source.title,
                subtitle = source.subtitle,
                artist = source.artist,
                genre = source.genre,
                level = source.level,
                mode_hint = source.mode_hint,
                md5 = source.md5,
                sha256 = source.sha256,
                banner = source.banner,
                backbmp = source.backbmp,
                stagefile = source.stagefile,
                preview_music = source.preview_music,
                updated_at = source.updated_at,
                wav_files = [.. (source.wav_files ?? [])],
                bga_files = [.. (source.bga_files ?? [])],
                UnsupportedResourceReferences = [.. (source.UnsupportedResourceReferences ?? [])],
                HasFreshResourceReferences = source.HasFreshResourceReferences,
                MaintenanceInfo = source.MaintenanceInfo?.CreatePersistenceCopy()
            };
        }

        private sealed class DetachedMergePackage
        {
            internal DetachedMergePackage(
                ChartPackage package,
                IReadOnlyDictionary<BMSFile, BMSFile> canonicalBmsByDetached,
                IReadOnlyDictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song> canonicalBmsonByDetached)
            {
                Package = package;
                CanonicalBmsByDetached = canonicalBmsByDetached;
                CanonicalBmsonByDetached = canonicalBmsonByDetached;
            }

            internal ChartPackage Package { get; }

            internal IReadOnlyDictionary<BMSFile, BMSFile> CanonicalBmsByDetached { get; }

            internal IReadOnlyDictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song> CanonicalBmsonByDetached { get; }
        }

        private void RunWithMergeDirectoryWriteLocks(long operationId, Action action)
        {
            Stopwatch initializedLockWaitStopwatch = Stopwatch.StartNew();
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            {
                BMSLibrary.LogInstallPerformance("duplicate_merge_model initialized_lock_acquired op=" + operationId + " waitMs=" + initializedLockWaitStopwatch.ElapsedMilliseconds);
                Stopwatch pendingLockWaitStopwatch = Stopwatch.StartNew();
                using (owner.rwlockPendingInstallCharts.GetWriterGuard())
                {
                    BMSLibrary.LogInstallPerformance("duplicate_merge_model pending_lock_acquired op=" + operationId + " waitMs=" + pendingLockWaitStopwatch.ElapsedMilliseconds);
                    Stopwatch bmsLockWaitStopwatch = Stopwatch.StartNew();
                    using (owner.rwlockBMSFiles.GetWriterGuard())
                    {
                        BMSLibrary.LogInstallPerformance("duplicate_merge_model bms_lock_acquired op=" + operationId + " waitMs=" + bmsLockWaitStopwatch.ElapsedMilliseconds);
                        action();
                    }
                }
            }
        }

        private bool MoveChartPackageFilesForMerge(
            ChartPackage package,
            string destinationDirectory,
            IPrimaryHashLookup existingHashes)
        {
            return owner.MoveChartPackageFiles(
                package,
                destinationDirectory,
                showMessageBoxOnInstallFail: false,
                deleteAllContents: true,
                existingHashes: existingHashes);
        }

        private DirectoryResourceLookupCache.ReverseLookupMutationResult RemoveReverseLookupDirectoriesUnderSource(string sourceDirectory)
        {
            DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            List<string> removedDirectories = [.. (owner.directoryResourceLookupCache?.Keys ?? [])
                .Where(path => (path + Path.DirectorySeparatorChar).StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
            foreach (string directory in removedDirectories)
            {
                mutation = mutation.Combine(owner.directoryResourceLookupCache.RemoveDirWithResult(directory));
            }
            return mutation;
        }

        private DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectories(ChartScanResult scan)
        {
            DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
            foreach (string chartDirectory in scan?.ChartDirectories ?? [])
            {
                mutation = mutation.Combine(owner.directoryResourceLookupCache.AddDir(chartDirectory, scan));
            }
            return mutation;
        }

        private void ApplyMergeFolderMaintenance(IEnumerable<ChartFile> charts)
        {
            owner.ApplyCatalogMaintenance(
                charts,
                forceUpdate: true,
                resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
                resourceHealthMutationReason: "merge_folder");
        }

        private void ShowFolderMergeFailed(string sourceDirectory, string destinationDirectory)
        {
            owner.ShowOperationDialog(
                string.Format(Resources.Error_BmsFolderMergeFailed, sourceDirectory, destinationDirectory),
                Resources.MessageBoxTitle_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK);
        }

        private static bool IsFilePathUnderDirectory(string filePath, string directoryPath)
        {
            return !string.IsNullOrWhiteSpace(filePath)
                && !string.IsNullOrWhiteSpace(directoryPath)
                && filePath.StartsWith(
                    directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
