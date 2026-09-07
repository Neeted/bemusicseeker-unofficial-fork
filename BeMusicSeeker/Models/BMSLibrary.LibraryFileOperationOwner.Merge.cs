using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using Ribbit.Logging;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryFileOperationOwner
{
    /// <summary>Returns merge facts and optionally leaves receipt-backed failure reporting to the operation terminal.</summary>
    internal DuplicateMergeMaintenanceReceipt MergeChartDirectory(string sourceDirectory, string destinationDirectory,
        long operationId, bool reportAtTerminal = false)
    {
        if (sourceDirectory == null)
        {
            throw new ArgumentNullException(nameof(sourceDirectory));
        }
        if (destinationDirectory == null)
        {
            throw new ArgumentNullException(nameof(destinationDirectory));
        }
        if (TryBlockMutation(nameof(BMSLibrary.MergeChartDirectory), showMessage: true))
        {
            return DuplicateMergeMaintenanceReceipt.NotApplied;
        }

        Stopwatch totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("duplicate_merge_model start op=" + operationId + " src=" + sourceDirectory + " dst=" + destinationDirectory);
        List<Action> postLeaseNotifications = [];
        try
        {
            return RunWithMergeDirectoryWriteLocks(operationId, mutationCapability =>
            {
                bool mergePrepared = false;
                List<ChartFile> preparedSourceCharts = [];
                IPrimaryHashLookup existingHashes = EmptyPrimaryHashLookup.Instance;
                LibraryMutationDelta catalogDelta = null;
                DetachedMergePackage detachedPackage = null;
                RunWithMergeSnapshotLocks(() =>
                {
                    List<ChartFile> sourceChartSnapshots = CreateOwnedRealPathChartSnapshotsUnsafe(sourceDirectory);
                    InstallDestinationOverlayChartRefSnapshot overlayChartRefs = CreateInstallDestinationOverlayChartRefSnapshot();
                    catalogDelta = PrepareMergeDirectory(
                        sourceDirectory,
                        destinationDirectory,
                        sourceChartSnapshots,
                        overlayChartRefs,
                        "duplicate_merge_prepare",
                        operationId,
                        out mergePrepared,
                        out preparedSourceCharts,
                        out existingHashes);
                    if (mergePrepared)
                    {
                        detachedPackage = CreateDetachedMergePackage(preparedSourceCharts, sourceDirectory);
                    }
                });
                if (!mergePrepared)
                {
                    LogInstallPerformance("duplicate_merge_model skipped op=" + operationId + " reason=no_source_charts totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    return () => DuplicateMergeMaintenanceReceipt.NotApplied;
                }

                ChartStorageTargetSet movedTargets = null;
                MaintenanceWorkflowResult maintenanceResult = null;
                List<Action> mutationPostLeaseNotifications = [];
                FileDbMutationReceipt mutationReceipt = null;
                mutationReceipt = packageInstallService.MovePackageFilesWithReceipt(
                    detachedPackage.Package,
                    destinationDirectory,
                    lr2SynchronizationOwner.CurrentOptionsSnapshot,
                    createChartFolderPathFromCharts,
                    DisplayedExceptionMessage.Format,
                    fileMutationService,
                    dialogService,
                    targetOnlyFileMutationOptions,
                    recursiveDirectoryTreeFileMutationOptions,
                    LogInstallPerformance,
                    installResult =>
                    {
                        if (catalogDelta == null)
                        {
                            return FileDbMutationCommitResult.Durable();
                        }
                        FileDbMutationCommitResult databaseResult = ApplyLibraryMutationDeltaForFileMutation(
                            catalogDelta,
                            "duplicate_merge_catalog_transition op=" + operationId,
                            suppressNormalRefreshNotification: false,
                            capability: mutationCapability,
                            postLeaseNotificationObserver: mutationPostLeaseNotifications.Add);
                        if (!databaseResult.DurableCommit)
                        {
                            return databaseResult;
                        }
                        List<ChartFile> destinationMaintenanceChartSnapshots = null;
                        Exception maintenanceInputCaptureFailure = null;
                        try
                        {
                            RunWithMergeSnapshotLocks(() =>
                            {
                                destinationMaintenanceChartSnapshots =
                                    CreateOwnedStorageTargetChartSnapshotsForSubtreeDirectoryUnsafe(destinationDirectory);
                            });
                        }
                        catch (Exception exception)
                        {
                            maintenanceInputCaptureFailure = exception;
                        }
                        Exception postCommitFailure = databaseResult.Failure;
                        if (maintenanceInputCaptureFailure != null)
                        {
                            postCommitFailure = postCommitFailure == null
                                ? maintenanceInputCaptureFailure
                                : new AggregateException(postCommitFailure, maintenanceInputCaptureFailure);
                        }
                        if (postCommitFailure == null)
                        {
                            postLeaseNotifications.Add(() =>
                            {
                                // The package executor may still report a durable
                                // finalization failure after this callback returns
                                // (for example when its live-package finalizer
                                // throws).  Gate all success publication on the
                                // terminal receipt that is available by the time
                                // the lease is released.
                                if (mutationReceipt?.TerminalState
                                    == FileDbMutationTerminalState.DurableFinalizationFailed)
                                {
                                    return;
                                }
                                InvokePostLeaseNotificationsBestEffort(mutationPostLeaseNotifications);
                                if (ChartDirectoryScanBuilder.TryBuildFromRoots(
                                    [destinationDirectory],
                                    out ChartScanResult scan,
                                    out string scanFailureReason))
                                {
                                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                                        resourceIndexOwner.ReplaceSourceDirectoryWithScan(sourceDirectory, scan).MutationResult;
                                    LogReverseLookupMutationAndQueueWarmupIfNeeded("merge_folder", reverseLookupMutation);
                                }
                                else
                                {
                                    LogInstallPerformanceWarning("duplicate_merge_model dst_scan_skipped op=" + operationId + " reason=incomplete_scan detail=" + (scanFailureReason ?? "unknown"));
                                }
                                // This callback runs after the merge lease is released.
                                // Unlike repair's in-lease recheck, merge maintenance
                                // must acquire its own reservation and defer index updates.
                                maintenanceResult = applyMergeFolderMaintenanceAfterRelease(destinationMaintenanceChartSnapshots);
                                LogInstallPerformance("duplicate_merge_model done op=" + operationId
                                    + " movedBms=" + (movedTargets?.BmsFiles.Count ?? 0)
                                    + " movedBmson=" + (movedTargets?.BmsonSongs.Count ?? 0)
                                    + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                            });
                        }
                        return FileDbMutationCommitResult.Durable(durableFailure: postCommitFailure);
                    },
                    showMessageBoxOnInstallFail: false,
                    deleteAllContents: true,
                    existingHashes: existingHashes,
                    onPreflightPrepared: installResult =>
                    {
                        List<ChartFile> destinationCharts = [.. (installResult?.AddedCharts ?? [])
                            .Where(chart => chart != null && IsFilePathUnderDirectory(chart.Path, destinationDirectory))];
                        using IDisposable destinationOwnerPaths = TemporarilyApplyDestinationStorageOwnerPaths(destinationCharts);
                        movedTargets = ChartStorageTargetSet.FromCharts(destinationCharts);
                        BuildMergeCatalogDelta(
                            catalogDelta,
                            preparedSourceCharts,
                            detachedPackage,
                            movedTargets);
                    });
                if (!mutationReceipt.DurableCommit
                    || mutationReceipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                {
                    return () =>
                    {
                        this.invalidateInstalledDirectoryIndex();
                        if (!reportAtTerminal)
                            postLeaseNotifications.Add(
                                () => ShowFolderMergeFailed(sourceDirectory, destinationDirectory));
                        InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
                        return CreateMergeMaintenanceReceipt(maintenanceResult, mutationReceipt);
                    };
                }
                return () =>
                {
                    InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
                    return CreateMergeMaintenanceReceipt(maintenanceResult, mutationReceipt, movedTargets);
                };
            });
        }
        catch (Exception ex)
        {
            this.invalidateInstalledDirectoryIndex();
            LogInstallPerformanceWarning("duplicate_merge_model failed op=" + operationId + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name);
            throw;
        }
    }

    private static DetachedMergePackage CreateDetachedMergePackage(
        IEnumerable<ChartFile> sourceCharts,
        string sourceDirectory)
    {
        var detachedBmsByCanonical = new Dictionary<BMSFile, BMSFile>();
        var detachedBmsonByCanonical = new Dictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song>();
        var canonicalBmsByDetached = new Dictionary<BMSFile, BMSFile>();
        var canonicalBmsonByDetached = new Dictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song>();
        List<PackageChartEntry> entries = [];
        foreach (ChartFile sourceChart in sourceCharts ?? [])
        {
            ChartFile chartSnapshot = sourceChart;
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

    private DuplicateMergeMaintenanceReceipt RunWithMergeDirectoryWriteLocks(
        long operationId,
        Func<LibraryFileMutationCapability, Func<DuplicateMergeMaintenanceReceipt>> action)
    {
        Func<DuplicateMergeMaintenanceReceipt> receiptFactory;
        using (LibraryFileMutationLease mutationLease = EnterMergeWriteScope())
        {
            if (mutationLease == null)
            {
                return DuplicateMergeMaintenanceReceipt.NotApplied;
            }
            using (LibraryFileMutationCapability capability = mutationLease.CreateMutationCapability())
            {
                capability.Validate(lr2SynchronizationOwner);
                receiptFactory = action(capability);
            }
        }
        return receiptFactory();
    }

    private static DuplicateMergeMaintenanceReceipt CreateMergeMaintenanceReceipt(
        MaintenanceWorkflowResult maintenanceResult,
        FileDbMutationReceipt mutationReceipt,
        ChartStorageTargetSet movedTargets = null)
    {
        maintenanceResult ??= new MaintenanceWorkflowResult();
        return new DuplicateMergeMaintenanceReceipt(
            mergeApplied: mutationReceipt?.DurableCommit == true
                && mutationReceipt.TerminalState != FileDbMutationTerminalState.DurableFinalizationFailed,
            intermediateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            maintenanceResult: MaintenanceWorkflowResultFacts.From(maintenanceResult),
            intermediateDeferred: maintenanceResult.ResourceHealthIndexDeferred,
            maintenanceHadUpdates: maintenanceResult.HasUpdates,
            resourceHealthIndexDeferred: maintenanceResult.ResourceHealthIndexDeferred,
            resourceHealthIndexDeltaApplied: maintenanceResult.ResourceHealthIndexDeltaApplied,
            resourceHealthIndexFullRebuilt: maintenanceResult.ResourceHealthIndexFullRebuilt,
            mutationReceipt: mutationReceipt);
    }

    private static void BuildMergeCatalogDelta(
        LibraryMutationDelta catalogDelta,
        IEnumerable<ChartFile> preparedSourceCharts,
        DetachedMergePackage detachedPackage,
        ChartStorageTargetSet movedTargets)
    {
        if (catalogDelta == null || detachedPackage == null)
        {
            return;
        }

        HashSet<string> sourceBmsPaths = new(
            (preparedSourceCharts ?? [])
                .Select(chart => chart?.GetBmsStorageOwner()?.path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> sourceBmsonPaths = new(
            (preparedSourceCharts ?? [])
                .Select(chart => chart?.GetBmsonStorageOwner()?.path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> movedBmsSourcePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> movedBmsonSourcePaths = new(StringComparer.OrdinalIgnoreCase);

        foreach (BMSFile movedBmsFile in movedTargets?.BmsFiles ?? [])
        {
            if (!detachedPackage.CanonicalBmsByDetached.TryGetValue(movedBmsFile, out BMSFile canonicalBmsFile)
                || string.IsNullOrWhiteSpace(canonicalBmsFile?.path)
                || string.IsNullOrWhiteSpace(movedBmsFile?.path))
            {
                continue;
            }
            movedBmsSourcePaths.Add(canonicalBmsFile.path);
            catalogDelta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(canonicalBmsFile),
                OldPath = canonicalBmsFile.path,
                NewPath = movedBmsFile.path
            });
        }
        foreach (LR2SongDBExtended.bmson_song movedBmsonSong in movedTargets?.BmsonSongs ?? [])
        {
            if (!detachedPackage.CanonicalBmsonByDetached.TryGetValue(movedBmsonSong, out LR2SongDBExtended.bmson_song canonicalBmsonSong)
                || string.IsNullOrWhiteSpace(canonicalBmsonSong?.path)
                || string.IsNullOrWhiteSpace(movedBmsonSong?.path))
            {
                continue;
            }
            movedBmsonSourcePaths.Add(canonicalBmsonSong.path);
            catalogDelta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsonStorageOwnerIdentity(canonicalBmsonSong),
                OldPath = canonicalBmsonSong.path,
                NewPath = movedBmsonSong.path
            });
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
    }

    private void ShowFolderMergeFailed(string sourceDirectory, string destinationDirectory)
    {
        ShowOperationDialog(
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

    private static IDisposable TemporarilyApplyDestinationStorageOwnerPaths(IEnumerable<ChartFile> charts)
    {
        return new DestinationStorageOwnerPathScope(charts);
    }

    private sealed class DestinationStorageOwnerPathScope : IDisposable
    {
        private readonly Dictionary<BMSFile, string> bmsPaths = [];
        private readonly Dictionary<LR2SongDBExtended.bmson_song, (string Path, string Folder)> bmsonPaths = [];
        private bool disposed;

        internal DestinationStorageOwnerPathScope(IEnumerable<ChartFile> charts)
        {
            try
            {
                foreach (ChartFile chart in charts ?? [])
                {
                    if (chart == null || string.IsNullOrWhiteSpace(chart.Path))
                    {
                        continue;
                    }

                    BMSFile bmsFile = chart.GetBmsStorageOwner();
                    if (bmsFile != null)
                    {
                        if (bmsPaths.TryAdd(bmsFile, bmsFile.path))
                        {
                            bmsFile.path = chart.Path;
                        }
                        continue;
                    }

                    LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
                    if (bmsonSong != null && bmsonPaths.TryAdd(
                        bmsonSong,
                        (bmsonSong.path, bmsonSong.folder)))
                    {
                        bmsonSong.path = chart.Path;
                        bmsonSong.folder = Path.GetDirectoryName(chart.Path) ?? string.Empty;
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach ((BMSFile bmsFile, string path) in bmsPaths)
            {
                bmsFile.path = path;
            }
            foreach ((LR2SongDBExtended.bmson_song bmsonSong, (string Path, string Folder) state) in bmsonPaths)
            {
                bmsonSong.path = state.Path;
                bmsonSong.folder = state.Folder;
            }
        }
    }
}
