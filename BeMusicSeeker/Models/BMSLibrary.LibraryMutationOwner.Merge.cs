using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>
    /// merge の確定事実を返し、必要に応じて receipt に基づく失敗表示を操作終端へ委ねます。
    /// </summary>
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
        if (TryBlockCatalogMutation(nameof(BMSLibrary.MergeChartDirectory), showMessage: true))
        {
            return DuplicateMergeMaintenanceReceipt.NotApplied;
        }

        Stopwatch totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("duplicate_merge_model start op=" + operationId + " src=" + sourceDirectory + " dst=" + destinationDirectory);
        List<Action> postLeaseNotifications = [];
        LibraryMutationSessionReceipt sessionReceipt = LibraryMutationSessionReceipt.Empty;
        List<ChartFile> destinationMaintenanceCharts = null;
        bool mergeApplied;
        try
        {
            using (LibraryFileMutationLease mutationLease = EnterMergeWriteScope())
            {
                if (mutationLease == null)
                {
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }
                using LibraryFileMutationCapability mutationCapability = mutationLease.CreateMutationCapability();
                mutationCapability.Validate(lr2SynchronizationOwner);
                bool mergePrepared = false;
                List<ChartFile> preparedSourceCharts = [];
                IPrimaryHashLookup existingHashes = EmptyPrimaryHashLookup.Instance;
                IInstalledChartLookupIndex independentOwnershipLookup = null;
                LibraryPackageReferenceFacts packageReferenceFacts = LibraryPackageReferenceFacts.Empty;
                DetachedMergePackage detachedPackage = null;
                RunWithMergeSnapshotLocks(() =>
                {
                    List<ChartFile> sourceChartSnapshots = CreateOwnedRealPathChartSnapshotsUnsafe(sourceDirectory);
                    InstallDestinationOverlayChartRefSnapshot overlayChartRefs = CreateInstallDestinationOverlayChartRefSnapshot();
                    packageReferenceFacts = PrepareMergeDirectory(
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
                        independentOwnershipLookup = CreateInstalledChartLookupSnapshotUnsafe();
                        detachedPackage = CreateDetachedMergePackage(preparedSourceCharts, sourceDirectory);
                    }
                });
                if (!mergePrepared)
                {
                    LogInstallPerformance("duplicate_merge_model skipped op=" + operationId + " reason=no_source_charts totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }

                LibraryMutationSession session = BeginLibraryMutationSession(
                    mutationCapability,
                    "duplicate_merge_catalog_transition op=" + operationId,
                    postLeaseNotifications,
                    suppressNormalRefreshNotification: false,
                    suppressLr2NormalFolderSync: false);
                LibraryCatalogMutationFacts catalogFacts = null;
                PackageInstallSessionMoveResult physicalMove = packageInstallService.MovePackageFilesForInstallSession(
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
                    sourceCleanupPolicy: PackageSourceCleanupPolicy.MergeOwnedSourceContents,
                    showMessageBoxOnInstallFail: false,
                    existingHashes: existingHashes,
                    independentOwnershipLookup: independentOwnershipLookup,
                    onPreflightPrepared: installResult =>
                    {
                        // 採番済み destination を変更前に固定し、storage owner の一時的な
                        // path 差替えや、physical success 後の destination 再計算を避けます。
                        catalogFacts = BuildMergeCatalogFacts(
                            preparedSourceCharts,
                            detachedPackage,
                            installResult.AddedCharts);
                    },
                    enqueueDiagnosticEffect: postLeaseNotifications.Add);
                if (physicalMove.Succeeded)
                {
                    session.AppendFolderMerge(
                        sourceDirectory,
                        destinationDirectory,
                        catalogFacts,
                        packageReferenceFacts,
                        physicalMove.PhysicalMutation);
                    session.AppendRequiredDurableFinalizer(() => RunWithMergeSnapshotLocks(() =>
                    {
                        destinationMaintenanceCharts =
                            CreateOwnedStorageTargetChartSnapshotsForSubtreeDirectoryUnsafe(destinationDirectory);
                    }));
                }
                else
                {
                    session.AppendPackagePhysicalFailure(physicalMove.FailureReceipt, physicalMove.IsPreflightRefusal);
                    if (!physicalMove.IsPreflightRefusal)
                    {
                        session.RecordStoppedSuffix(
                            sourceDirectory,
                            destinationDirectory,
                            physicalMove.FailureReceipt.Failure,
                            []);
                    }
                }
                sessionReceipt = session.Commit();
                mergeApplied = sessionReceipt.DurableCommit && !sessionReceipt.HasDurableFinalizationFailure;
            }

            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
            MaintenanceWorkflowResult maintenanceResult = null;
            if (mergeApplied)
            {
                // maintenance は独自の既存 reservation を取得するため、merge lease の
                // 解放後に実行します。任意通知ではなく同じ session の PostCommitMaintenance
                // phase とし、失敗しても確定済み merge と cleanup の結果を失いません。
                try
                {
                    maintenanceResult = applyMergeFolderMaintenanceAfterRelease(destinationMaintenanceCharts);
                    if (maintenanceResult.Canceled)
                    {
                        sessionReceipt = sessionReceipt.WithFinalizationFailure(
                            new OperationCanceledException(Resources.Error_MergePostCommitMaintenanceIncomplete));
                    }
                }
                catch (Exception exception)
                {
                    sessionReceipt = sessionReceipt.WithFinalizationFailure(exception);
                }
                LogInstallPerformance("duplicate_merge_model done op=" + operationId
                    + " movedCharts=" + sessionReceipt.CatalogChartPathChangeCount
                    + " maintenanceFailed=" + sessionReceipt.HasDurableFinalizationFailure
                    + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
            }
            else
            {
                InvalidateInstalledDirectoryIndex();
            }
            if (!reportAtTerminal && (!mergeApplied || sessionReceipt.HasRequiredFailure))
            {
                InvokePostLeaseNotificationsBestEffort(
                    [() => ShowFolderMergeFailed(sourceDirectory, destinationDirectory)]);
            }
            return CreateMergeMaintenanceReceipt(maintenanceResult, sessionReceipt, mergeApplied);
        }
        catch (Exception ex)
        {
            InvalidateInstalledDirectoryIndex();
            LogInstallPerformanceWarning("duplicate_merge_model failed op=" + operationId + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " exception=" + ex.GetType().Name);
            throw;
        }
    }

    private static DetachedMergePackage CreateDetachedMergePackage(
        IEnumerable<ChartFile> sourceCharts,
        string sourceDirectory)
    {
        var canonicalBmsByDetached = new Dictionary<BMSFile, BMSFile>();
        var canonicalBmsonByDetached = new Dictionary<LR2SongDBExtended.bmson_song, LR2SongDBExtended.bmson_song>();
        var sourceFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<PackageChartEntry> entries = [];
        foreach (ChartFile sourceChart in sourceCharts ?? [])
        {
            ChartFile chartSnapshot = sourceChart;
            if (chartSnapshot == null)
            {
                continue;
            }
            // 起動時scanを省略した旧DBではdot別表記も残る。物理packageのpathを
            // component列挙と揃え、同じ実ファイルの二重予約を防ぐ。旧exact keyは
            // canonical ownerとpreparedSourceChartsに残し、catalog deltaだけで扱う。
            string sourceFilePath = LongPathFileSystem.NormalizePathForStorage(chartSnapshot.Path);
            if (!sourceFilePaths.Add(sourceFilePath))
            {
                continue;
            }

            ChartFile detachedChart = chartSnapshot;
            BMSFile canonicalBmsFile = sourceChart.GetBmsStorageOwner();
            if (canonicalBmsFile != null)
            {
                BMSFile detachedBmsFile = canonicalBmsFile.CreateSongRowPersistenceCopy();
                detachedBmsFile.path = sourceFilePath;
                canonicalBmsByDetached[detachedBmsFile] = canonicalBmsFile;
                detachedChart = CreateDetachedChart(chartSnapshot, sourceFilePath, detachedBmsFile, null);
            }
            else
            {
                LR2SongDBExtended.bmson_song canonicalBmsonSong = sourceChart.GetBmsonStorageOwner();
                if (canonicalBmsonSong != null)
                {
                    LR2SongDBExtended.bmson_song detachedBmsonSong = CreateBmsonPersistenceCopy(canonicalBmsonSong);
                    detachedBmsonSong.path = sourceFilePath;
                    canonicalBmsonByDetached[detachedBmsonSong] = canonicalBmsonSong;
                    detachedChart = CreateDetachedChart(chartSnapshot, sourceFilePath, null, detachedBmsonSong);
                }
            }

            PackageChartEntry entry = PackageChartEntry.FromChart(detachedChart);
            if (entry != null)
            {
                entries.Add(entry);
            }
        }

        ChartPackage package = ChartPackage.FromChartEntries(entries);
        package.path = LongPathFileSystem.NormalizePathForStorage(sourceDirectory);
        package.delete_parent = false;
        return new DetachedMergePackage(
            package,
            canonicalBmsByDetached,
            canonicalBmsonByDetached);
    }

    private static ChartFile CreateDetachedChart(
        ChartFile source,
        string sourceFilePath,
        BMSFile bmsFile,
        LR2SongDBExtended.bmson_song bmsonSong)
    {
        return new ChartFile(
            source.Kind,
            sourceFilePath,
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

    private static DuplicateMergeMaintenanceReceipt CreateMergeMaintenanceReceipt(
        MaintenanceWorkflowResult maintenanceResult,
        LibraryMutationSessionReceipt sessionReceipt,
        bool mergeApplied)
    {
        maintenanceResult ??= new MaintenanceWorkflowResult();
        return new DuplicateMergeMaintenanceReceipt(
            mergeApplied: mergeApplied,
            intermediateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            maintenanceResult: MaintenanceWorkflowResultFacts.From(maintenanceResult),
            intermediateDeferred: maintenanceResult.ResourceHealthIndexDeferred,
            maintenanceHadUpdates: maintenanceResult.HasUpdates,
            resourceHealthIndexDeferred: maintenanceResult.ResourceHealthIndexDeferred,
            resourceHealthIndexDeltaApplied: maintenanceResult.ResourceHealthIndexDeltaApplied,
            resourceHealthIndexFullRebuilt: maintenanceResult.ResourceHealthIndexFullRebuilt,
            sessionReceipt: sessionReceipt);
    }

    private static LibraryCatalogMutationFacts BuildMergeCatalogFacts(
        IEnumerable<ChartFile> preparedSourceCharts,
        DetachedMergePackage detachedPackage,
        IEnumerable<ChartFile> movedCharts)
    {
        if (detachedPackage == null)
        {
            return LibraryCatalogMutationFacts.Empty;
        }

        var pathChanges = new List<LibraryChartPathChange>();
        var removalRequests = new List<OwnedChartRemoveRequest>();

        HashSet<string> sourceBmsPaths = new(
            (preparedSourceCharts ?? [])
                .Select(chart => chart?.GetBmsStorageOwner()?.path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        HashSet<string> sourceBmsonPaths = new(
            (preparedSourceCharts ?? [])
                .Select(chart => chart?.GetBmsonStorageOwner()?.path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        HashSet<string> movedBmsSourcePaths = new(StringComparer.Ordinal);
        HashSet<string> movedBmsonSourcePaths = new(StringComparer.Ordinal);

        foreach (ChartFile movedChart in movedCharts ?? [])
        {
            BMSFile movedBmsFile = movedChart?.GetBmsStorageOwner();
            LR2SongDBExtended.bmson_song movedBmsonSong = movedChart?.GetBmsonStorageOwner();
            if (movedBmsFile != null
                && detachedPackage.CanonicalBmsByDetached.TryGetValue(movedBmsFile, out BMSFile canonicalBmsFile))
            {
                movedBmsSourcePaths.Add(canonicalBmsFile.path);
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsStorageOwnerIdentity(canonicalBmsFile),
                    OldPath = canonicalBmsFile.path,
                    NewPath = movedChart.Path
                });
            }
            else if (movedBmsonSong != null
                && detachedPackage.CanonicalBmsonByDetached.TryGetValue(movedBmsonSong, out LR2SongDBExtended.bmson_song canonicalBmsonSong))
            {
                movedBmsonSourcePaths.Add(canonicalBmsonSong.path);
                pathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonStorageOwnerIdentity(canonicalBmsonSong),
                    OldPath = canonicalBmsonSong.path,
                    NewPath = movedChart.Path
                });
            }
        }
        foreach (string sourcePath in sourceBmsPaths.Where(path => !movedBmsSourcePaths.Contains(path)))
        {
            removalRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bms, sourcePath));
        }
        foreach (string sourcePath in sourceBmsonPaths.Where(path => !movedBmsonSourcePaths.Contains(path)))
        {
            removalRequests.Add(OwnedChartRemoveRequest.FromPathCleanup(ChartFileKind.Bmson, sourcePath));
        }
        return new LibraryCatalogMutationFacts(removalRequests, pathChanges, []);
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

}
