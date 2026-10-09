using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
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
        long operationId, bool reportAtTerminal = false, LibraryFileMutationCapability capability = null)
    {
        if (sourceDirectory == null)
        {
            throw new ArgumentNullException(nameof(sourceDirectory));
        }
        if (destinationDirectory == null)
        {
            throw new ArgumentNullException(nameof(destinationDirectory));
        }
        if (TryBlockCatalogMutation(nameof(BMSLibrary.MergeChartDirectory), showMessage: true, capability: capability))
        {
            return DuplicateMergeMaintenanceReceipt.NotApplied;
        }

        var totalStopwatch = Stopwatch.StartNew();
        LogInstallPerformance("duplicate_merge_model start op=" + operationId + " src=" + sourceDirectory + " dst=" + destinationDirectory);
        List<Action> postLeaseNotifications = [];
        LibraryMutationSessionReceipt sessionReceipt = LibraryMutationSessionReceipt.Empty;
        List<ChartFile> destinationMaintenanceCharts = null;
        bool mergeApplied;
        MaintenanceWorkflowResult maintenanceResult = null;
        try
        {
            using (LibraryFileMutationLease mutationLease = synchronization.EnterMergeWriteScope(capability))
            {
                if (mutationLease == null)
                {
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }
                if (!TryEnterManagedOutputMutation([sourceDirectory, destinationDirectory], recursive: true, out LibraryFileMutationLease playlistLease, capability))
                {
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }
                using LibraryFileMutationLease playlistOperation = playlistLease;
                using LibraryFileMutationCapability primaryCapability = mutationLease.CreateMutationCapability();
                using LibraryFileMutationCapability playlistCapability = playlistLease?.CreateMutationCapability();
                using LibraryFileMutationCapability mutationCapability = primaryCapability.WithPlaylistCapability(playlistCapability);
                mutationCapability.Validate(lr2SynchronizationOwner.OperationAdmission);
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

                using LibraryMutationSession session = BeginLibraryMutationSession(
                    mutationCapability,
                    "duplicate_merge_catalog_transition op=" + operationId,
                    postLeaseNotifications);
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
                if (mergeApplied)
                {
                    // 必須保守は同じ受理操作の生存権限を借用します。通知を既存の延期先へ
                    // 集め、確定済み統合と後片付けの事実は保守失敗でも保持します。
                    try
                    {
                        maintenanceResult = applyMergeFolderMaintenance(destinationMaintenanceCharts, mutationCapability, postLeaseNotifications.Add);
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
                }
            }

            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
            if (mergeApplied)
            {
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
        var canonicalByToken = new Dictionary<OwnedChartToken, ChartFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<PackageChartEntry> entries = [];
        foreach (ChartFile chart in sourceCharts ?? [])
        {
            if (chart == null)
            {
                continue;
            }

            string path = LongPathFileSystem.NormalizePathForStorage(chart.Path);
            if (!paths.Add(path))
            {
                continue;
            }

            if (chart.Token != null)
            {
                canonicalByToken.Add(chart.Token, chart);
            }

            entries.Add(PackageChartEntry.FromChart(chart with { Path = path }));
        }
        var package = ChartPackage.FromChartEntries(entries);
        package.path = LongPathFileSystem.NormalizePathForStorage(sourceDirectory);
        package.delete_parent = false;
        return new DetachedMergePackage(package, canonicalByToken);
    }

    /// <summary>物理準備中のentryと、同じ所持識別の確定前共通値を保持します。</summary>
    private sealed class DetachedMergePackage(ChartPackage package, IReadOnlyDictionary<OwnedChartToken, ChartFile> canonicalByToken)
    {
        internal ChartPackage Package { get; } = package;
        internal IReadOnlyDictionary<OwnedChartToken, ChartFile> CanonicalByToken { get; } = canonicalByToken;
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
                .Where(chart => chart?.Kind == ChartFileKind.Bms).Select(chart => chart.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        HashSet<string> sourceBmsonPaths = new(
            (preparedSourceCharts ?? [])
                .Where(chart => chart?.Kind == ChartFileKind.Bmson).Select(chart => chart.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        HashSet<string> movedBmsSourcePaths = new(StringComparer.Ordinal);
        HashSet<string> movedBmsonSourcePaths = new(StringComparer.Ordinal);

        foreach (ChartFile movedChart in movedCharts ?? [])
        {
            if (movedChart?.Token == null || !detachedPackage.CanonicalByToken.TryGetValue(movedChart.Token, out ChartFile canonical))
            {
                continue;
            }

            (canonical.Kind == ChartFileKind.Bms ? movedBmsSourcePaths : movedBmsonSourcePaths).Add(canonical.Path);
            pathChanges.Add(new LibraryChartPathChange { Chart = canonical, OldPath = canonical.Path, NewPath = movedChart.Path });
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
