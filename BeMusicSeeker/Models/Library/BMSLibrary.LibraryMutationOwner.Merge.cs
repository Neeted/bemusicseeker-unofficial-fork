using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryMutationOwner
{
    /// <summary>
    /// merge の確定事実を返し、必要に応じて receipt に基づく失敗表示を操作終端へ委ねます。
    /// 確定利用者登録本体や空の登録子を含む祖先を元にする要求は、物理準備・変更セッションの開始前に拒否します。
    /// 統合元の本体と配下の全階層、統合先の本体、統合元の項目と同じ相対パスにある統合先の既存項目がリンク
    /// （シンボリックリンク・ジャンクション）の場合と、それを確認できない場合も、同じ時点で案内一件だけを加えて拒否します。
    /// </summary>
    /// <param name="notices">この操作の案内を集める入れ物。拒否・失敗の案内を加え、呼出元が受付の解放後に表示します。</param>
    internal DuplicateMergeMaintenanceReceipt MergeChartDirectory(string sourceDirectory, string destinationDirectory,
        long operationId, LibraryOperationNotices notices, bool reportAtTerminal = false, LibraryFileMutationCapability capability = null)
    {
        ArgumentNullException.ThrowIfNull(notices);
        if (sourceDirectory == null)
        {
            throw new ArgumentNullException(nameof(sourceDirectory));
        }
        if (destinationDirectory == null)
        {
            throw new ArgumentNullException(nameof(destinationDirectory));
        }
        if (TryBlockCatalogMutation(nameof(BMSLibrary.MergeChartDirectory), notices, capability: capability))
        {
            return DuplicateMergeMaintenanceReceipt.NotApplied;
        }
        if (ContainsRegisteredBmsRoot(sourceDirectory))
        {
            AddCannotMoveRegisteredBmsRootNotice(notices, sourceDirectory);
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
            using (LibraryFileMutationLease mutationLease = synchronization.EnterMergeWriteScope(notices, capability))
            {
                if (mutationLease == null)
                {
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }
                if (!TryEnterManagedOutputMutation([sourceDirectory, destinationDirectory], recursive: true, notices, out LibraryFileMutationLease playlistLease, capability))
                {
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }
                using LibraryFileMutationLease playlistOperation = playlistLease;
                using LibraryFileMutationCapability primaryCapability = mutationLease.CreateMutationCapability();
                using LibraryFileMutationCapability playlistCapability = playlistLease?.CreateMutationCapability();
                using LibraryFileMutationCapability mutationCapability = primaryCapability.WithPlaylistCapability(playlistCapability);
                mutationCapability.Validate(lr2SynchronizationOwner.OperationAdmission);
                // リンクのファイルを作品の一部として移動・削除しないよう、準備・変更セッションの開始・物理変更より前に拒否します。
                // 受付とBusyの拒否の後で行い、拒否は案内一件だけで統合失敗の案内・変更セッションの事実を作りません。
                FolderLinkInspection linkInspection = FolderLinkInspector.InspectMerge(sourceDirectory, destinationDirectory);
                if (linkInspection.Outcome != FolderLinkInspectionOutcome.Clear)
                {
                    AddMergeLinkRefusalNotice(notices, linkInspection);
                    LogInstallPerformanceWarning("duplicate_merge_model refused op=" + operationId
                        + " reason=" + (linkInspection.Outcome == FolderLinkInspectionOutcome.LinkFound ? "link" : "link_check_failed")
                        + " path=" + linkInspection.Path
                        + (linkInspection.Failure == null ? string.Empty : " exception=" + linkInspection.Failure.GetType().Name)
                        + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
                    return DuplicateMergeMaintenanceReceipt.NotApplied;
                }
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
                    postLeaseNotifications,
                    notices);
                PackageInstallSessionMoveResult physicalMove = MoveMergeSourceForSession(
                    detachedPackage,
                    preparedSourceCharts,
                    destinationDirectory,
                    notices,
                    postLeaseNotifications,
                    existingHashes,
                    independentOwnershipLookup,
                    onMutationScopePrepared: null,
                    out LibraryCatalogMutationFacts catalogFacts);
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
                    maintenanceResult = RunMergeFolderMaintenance(
                        destinationMaintenanceCharts, mutationCapability, postLeaseNotifications, notices, ref sessionReceipt);
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
                AddFolderMergeFailedNotice(notices, sourceDirectory, destinationDirectory);
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

    /// <summary>
    /// 一つの統合元の物理処理（保留導入と同じ共通のパッケージ処理）を実行します。単体の統合と一括整理が同じ処理・設定を使います。
    /// 案内は積まず、採番済みの移動先から作ったカタログ事実を、変更前に固定して返します。
    /// </summary>
    /// <param name="existingHashes">既に所持している主ハッシュの判定。該当する譜面は移動しません。</param>
    /// <param name="independentOwnershipLookup">入力元の残存物を削除してよい裏付けとなる、独立した所持の判定。</param>
    /// <param name="onMutationScopePrepared">読み取りだけの事前確認の後、最初の物理変更の前に呼びます。false を返すと変更せずに事前拒否として戻ります。</param>
    /// <param name="catalogFacts">採番済みの移動先から作ったカタログ事実。事前確認の前に拒否した場合は null です。</param>
    private PackageInstallSessionMoveResult MoveMergeSourceForSession(
        DetachedMergePackage detachedPackage,
        IEnumerable<ChartFile> preparedSourceCharts,
        string destinationDirectory,
        LibraryOperationNotices notices,
        ICollection<Action> postLeaseNotifications,
        IPrimaryHashLookup existingHashes,
        IInstalledChartLookupIndex independentOwnershipLookup,
        Func<string, string, bool> onMutationScopePrepared,
        out LibraryCatalogMutationFacts catalogFacts)
    {
        LibraryCatalogMutationFacts facts = null;
        PackageInstallSessionMoveResult physicalMove = packageInstallService.MovePackageFilesForInstallSession(
            detachedPackage.Package,
            destinationDirectory,
            lr2SynchronizationOwner.CurrentOptionsSnapshot,
            createChartFolderPathFromCharts,
            DisplayedExceptionMessage.Format,
            fileMutationService,
            notices,
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
                facts = BuildMergeCatalogFacts(
                    preparedSourceCharts,
                    detachedPackage,
                    installResult.AddedCharts);
            },
            enqueueDiagnosticEffect: postLeaseNotifications.Add,
            onMutationScopePrepared: onMutationScopePrepared);
        catalogFacts = facts;
        return physicalMove;
    }

    /// <summary>
    /// 確定後の必須保守を、統合と同じ生存権限で一回だけ実行します。取消は渡しません。
    /// 保守の失敗・取消は確定済みの統合を取り消さず、完全成功として報告しないよう終端の証票へ必須失敗として加えます。
    /// </summary>
    /// <param name="destinationMaintenanceCharts">統合先の保守対象。後片付けの前に固定した現在値です。</param>
    /// <param name="sessionReceipt">保守の失敗を必須失敗として加える、この操作の変更セッションの証票。</param>
    /// <returns>保守の結果。保守が例外で終わった場合は null です。</returns>
    private MaintenanceWorkflowResult RunMergeFolderMaintenance(
        IEnumerable<ChartFile> destinationMaintenanceCharts,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        LibraryOperationNotices notices,
        ref LibraryMutationSessionReceipt sessionReceipt)
    {
        MaintenanceWorkflowResult maintenanceResult = null;
        var readFailures = new MaintenanceReadFailureCollector();
        try
        {
            maintenanceResult = applyMergeFolderMaintenance(destinationMaintenanceCharts, mutationCapability, postLeaseNotifications.Add, readFailures);
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
        NotifyMaintenanceReadFailures(readFailures, notices);
        return maintenanceResult;
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

    /// <summary>リンクを含む、またはリンクの有無を確認できない統合の拒否理由を、この操作の収集先へ一件だけ加えます。</summary>
    private static void AddMergeLinkRefusalNotice(LibraryOperationNotices notices, FolderLinkInspection inspection)
        => AddNotice(
            notices,
            string.Format(
                inspection.Outcome == FolderLinkInspectionOutcome.LinkFound
                    ? Resources.Warn_BmsFolderMergeLinkFound
                    : Resources.Warn_BmsFolderMergeLinkCheckFailed,
                inspection.Path),
            Resources.MessageBoxTitle_Warning,
            MessageBoxImage.Exclamation);

    private static void AddFolderMergeFailedNotice(LibraryOperationNotices notices, string sourceDirectory, string destinationDirectory)
        => AddNotice(notices, string.Format(Resources.Error_BmsFolderMergeFailed, sourceDirectory, destinationDirectory),
            Resources.MessageBoxTitle_Error, MessageBoxImage.Hand);

}
