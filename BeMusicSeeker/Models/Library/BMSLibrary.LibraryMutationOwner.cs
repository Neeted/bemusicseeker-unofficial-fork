using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.Models;

/// <summary>
/// file mutation、導入、走査、metadataの確定事実から、catalog・索引・参照の共通反映と公開順序を管理します。
/// 各専門ownerを明示依存として使い、集約facadeや反映代行用のoperation portは保持しません。
/// </summary>
internal sealed partial class LibraryMutationOwner
{
    private readonly LibraryFileOperationSynchronization synchronization;

    private readonly BmsLibraryLibraryFileOperationsService libraryFileOperationsService;

    private readonly BmsLibraryPackageInstallService packageInstallService;

    private readonly PackageLifecycleOwner packageLifecycleOwner;

    private readonly LibraryResourceIndexOwner resourceIndexOwner;

    private readonly IFileMutationService fileMutationService;

    private readonly InstallDestinationStateOwner installDestinationStateOwner;

    private readonly ScopedOperationDialogCoordinator dialogService;

    private readonly BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner;

    private readonly CatalogOwnedCollectionOwner catalogOwnedCollectionOwner;

    private readonly CatalogStorageRowsOwner catalogStorageRowsOwner;

    private readonly CatalogMutationOwner catalogMutationOwner;

    private readonly CatalogMaintenanceOwner catalogMaintenanceOwner;

    private readonly ResourceHealthIndexOwner resourceHealthOwner;

    private readonly BmsLibraryPlaylistReferenceOwner playlistReferenceOwner;

    private readonly object pendingInstallEstimateCurrentnessGate;

    private readonly NormalLibraryRefreshPublisher normalLibraryRefreshPublisher;

    private readonly Func<CatalogInstalledTargetUpsertReceipt, int, Lr2NormalFolderCatalogMutationReceipt> createInstalledTargetLr2NormalFolderMutationReceipt;

    private readonly Func<CatalogMutationReceipt, int, Lr2NormalFolderCatalogMutationReceipt> createCatalogLr2NormalFolderMutationReceipt;

    private readonly Func<bool, bool> invalidateDuplicateChartGroupsCache;

    private readonly Action raiseDuplicateChartGroupsChanged;

    private readonly Action markDuplicateWarningFullClearPending;

    private readonly Action invalidateParentFolderListCache;

    private readonly Action notifyParentFolderListCacheChanged;

    private readonly Action raiseOwnedCollectionVersionChanged;

    private readonly Action raiseNormalLibraryRefreshVersionChanged;

    private readonly Action<bool, bool> notifyStorageRowsChanged;

    private readonly Action invalidateInstallEstimationMetadataProfileCache;

    private readonly Func<string, ResourceMaintenanceTargetSet> createFullOwnedResourceMaintenanceTargetSet;

    private readonly Action<string, string> logStartupMemoryCheckpoint;

    private long ownedDigestMutationGeneration;

    private readonly Func<IEnumerable<ChartFile>, string, string> createChartFolderPathFromCharts;

    private readonly Func<ChartFile, IEnumerable<string>> getDuplicateInstallRepairPaths;

    private readonly Func<IEnumerable<ChartFile>, MaintenanceWorkflowResult> applyMergeFolderMaintenanceAfterRelease;

    private readonly Action<string, DirectoryResourceLookupCache.ReverseLookupMutationResult> logReverseLookupMutationAndQueueWarmupIfNeeded;

    private readonly Action<string> logInstallPerformance;

    private readonly Action<string> logInstallPerformanceWarning;

    private readonly Action<string> logFileInfo;

    private readonly Action<Exception, string> logFileWarning;

    private readonly FileMutationOptions targetOnlyFileMutationOptions;

    private readonly FileMutationOptions recursiveDirectoryTreeFileMutationOptions;

    private readonly AutoRenameBatchCoordinator autoRenameBatchCoordinator;

    private sealed class LibraryChartRemovalPreflight
    {
        internal IReadOnlyList<LibraryFileOperationTargetSnapshot> Targets { get; init; } = [];

        internal IReadOnlyList<string> WholeFolderCandidatePaths { get; init; } = [];

        internal IReadOnlyList<string> UnresolvedPaths { get; init; } = [];
    }

    private sealed class LibraryChartRemovalInstallDestinationBinding
    {
        internal string FolderPath { get; init; }

        internal LibraryChartKind Kind { get; init; }

        internal string Path { get; init; }

        internal string Md5 { get; init; }

        internal string Sha256 { get; init; }

        internal PackageChartEntry Entry { get; init; }

        internal ChartFile Chart { get; init; }
    }

    private sealed class DetachedFixTarget
    {
        internal LibraryFileOperationTargetSnapshot Original { get; init; }

        internal ChartFile DetachedChart { get; init; }
    }

    /// <summary>
    /// canonical owner と file/package service を結び、library mutation owner を構築します。
    /// file mutation は外側の lease から受けた capability を使って LR2 bridge と
    /// catalog apply の所有範囲を共有します。repair maintenance は同じ予約を再利用し、
    /// merge maintenance は lease 解放後に別の通常 maintenance 予約を取得します。
    /// </summary>
    /// <remarks>
    /// CatalogOwnedCollectionOwner、CatalogStorageRowsOwner、CatalogMutationOwner、
    /// CatalogMaintenanceOwner、ResourceHealthIndexOwner、PlaylistReferenceOwner、
    /// InstallDestinationStateOwner、PackageLifecycleOwner、ResourceIndexOwner、
    /// LR2 synchronization owner への依存をここで明示します。lookup state の構築、
    /// mutation facts の判定、適用順序、currentness gate の境界はこの owner が所有し、
    /// 外部へは確定 receipt と lease 解放後の action だけを返します。
    /// </remarks>
    internal LibraryMutationOwner(
        LibraryFileOperationSynchronization synchronization,
        BmsLibraryLibraryFileOperationsService libraryFileOperationsService,
        BmsLibraryPackageInstallService packageInstallService,
        PackageLifecycleOwner packageLifecycleOwner,
        LibraryResourceIndexOwner resourceIndexOwner,
        IFileMutationService fileMutationService,
        InstallDestinationStateOwner installDestinationStateOwner,
        ScopedOperationDialogCoordinator dialogService,
        BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner,
        CatalogOwnedCollectionOwner catalogOwnedCollectionOwner,
        CatalogStorageRowsOwner catalogStorageRowsOwner,
        CatalogMutationOwner catalogMutationOwner,
        CatalogMaintenanceOwner catalogMaintenanceOwner,
        ResourceHealthIndexOwner resourceHealthOwner,
        BmsLibraryPlaylistReferenceOwner playlistReferenceOwner,
        object pendingInstallEstimateCurrentnessGate,
        NormalLibraryRefreshPublisher normalLibraryRefreshPublisher,
        Func<CatalogInstalledTargetUpsertReceipt, int, Lr2NormalFolderCatalogMutationReceipt> createInstalledTargetLr2NormalFolderMutationReceipt,
        Func<CatalogMutationReceipt, int, Lr2NormalFolderCatalogMutationReceipt> createCatalogLr2NormalFolderMutationReceipt,
        Func<bool, bool> invalidateDuplicateChartGroupsCache,
        Action raiseDuplicateChartGroupsChanged,
        Action markDuplicateWarningFullClearPending,
        Action invalidateParentFolderListCache,
        Action notifyParentFolderListCacheChanged,
        Action raiseOwnedCollectionVersionChanged,
        Action raiseNormalLibraryRefreshVersionChanged,
        Action<bool, bool> notifyStorageRowsChanged,
        Action invalidateInstallEstimationMetadataProfileCache,
        Func<string, ResourceMaintenanceTargetSet> createFullOwnedResourceMaintenanceTargetSet,
        Action<string, string> logStartupMemoryCheckpoint,
        Func<IEnumerable<ChartFile>, string, string> createChartFolderPathFromCharts,
        Func<ChartFile, IEnumerable<string>> getDuplicateInstallRepairPaths,
        Func<IEnumerable<ChartFile>, MaintenanceWorkflowResult> applyMergeFolderMaintenanceAfterRelease,
        Action<string, DirectoryResourceLookupCache.ReverseLookupMutationResult> logReverseLookupMutationAndQueueWarmupIfNeeded,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarning,
        Action<string> logFileInfo,
        Action<Exception, string> logFileWarning,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions)
    {
        this.synchronization = synchronization ?? throw new ArgumentNullException(nameof(synchronization));
        this.libraryFileOperationsService = libraryFileOperationsService ?? throw new ArgumentNullException(nameof(libraryFileOperationsService));
        this.packageInstallService = packageInstallService ?? throw new ArgumentNullException(nameof(packageInstallService));
        this.packageLifecycleOwner = packageLifecycleOwner ?? throw new ArgumentNullException(nameof(packageLifecycleOwner));
        this.resourceIndexOwner = resourceIndexOwner ?? throw new ArgumentNullException(nameof(resourceIndexOwner));
        this.fileMutationService = fileMutationService ?? throw new ArgumentNullException(nameof(fileMutationService));
        this.installDestinationStateOwner = installDestinationStateOwner ?? throw new ArgumentNullException(nameof(installDestinationStateOwner));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.lr2SynchronizationOwner = lr2SynchronizationOwner ?? throw new ArgumentNullException(nameof(lr2SynchronizationOwner));
        this.catalogOwnedCollectionOwner = catalogOwnedCollectionOwner ?? throw new ArgumentNullException(nameof(catalogOwnedCollectionOwner));
        this.catalogStorageRowsOwner = catalogStorageRowsOwner ?? throw new ArgumentNullException(nameof(catalogStorageRowsOwner));
        this.catalogMutationOwner = catalogMutationOwner ?? throw new ArgumentNullException(nameof(catalogMutationOwner));
        this.catalogMaintenanceOwner = catalogMaintenanceOwner ?? throw new ArgumentNullException(nameof(catalogMaintenanceOwner));
        this.resourceHealthOwner = resourceHealthOwner ?? throw new ArgumentNullException(nameof(resourceHealthOwner));
        this.playlistReferenceOwner = playlistReferenceOwner ?? throw new ArgumentNullException(nameof(playlistReferenceOwner));
        this.pendingInstallEstimateCurrentnessGate = pendingInstallEstimateCurrentnessGate ?? throw new ArgumentNullException(nameof(pendingInstallEstimateCurrentnessGate));
        this.normalLibraryRefreshPublisher = normalLibraryRefreshPublisher ?? throw new ArgumentNullException(nameof(normalLibraryRefreshPublisher));
        this.createInstalledTargetLr2NormalFolderMutationReceipt = createInstalledTargetLr2NormalFolderMutationReceipt ?? throw new ArgumentNullException(nameof(createInstalledTargetLr2NormalFolderMutationReceipt));
        this.createCatalogLr2NormalFolderMutationReceipt = createCatalogLr2NormalFolderMutationReceipt ?? throw new ArgumentNullException(nameof(createCatalogLr2NormalFolderMutationReceipt));
        this.invalidateDuplicateChartGroupsCache = invalidateDuplicateChartGroupsCache ?? throw new ArgumentNullException(nameof(invalidateDuplicateChartGroupsCache));
        this.raiseDuplicateChartGroupsChanged = raiseDuplicateChartGroupsChanged ?? throw new ArgumentNullException(nameof(raiseDuplicateChartGroupsChanged));
        this.markDuplicateWarningFullClearPending = markDuplicateWarningFullClearPending ?? throw new ArgumentNullException(nameof(markDuplicateWarningFullClearPending));
        this.invalidateParentFolderListCache = invalidateParentFolderListCache ?? throw new ArgumentNullException(nameof(invalidateParentFolderListCache));
        this.notifyParentFolderListCacheChanged = notifyParentFolderListCacheChanged ?? throw new ArgumentNullException(nameof(notifyParentFolderListCacheChanged));
        this.raiseOwnedCollectionVersionChanged = raiseOwnedCollectionVersionChanged ?? throw new ArgumentNullException(nameof(raiseOwnedCollectionVersionChanged));
        this.raiseNormalLibraryRefreshVersionChanged = raiseNormalLibraryRefreshVersionChanged ?? throw new ArgumentNullException(nameof(raiseNormalLibraryRefreshVersionChanged));
        this.notifyStorageRowsChanged = notifyStorageRowsChanged ?? throw new ArgumentNullException(nameof(notifyStorageRowsChanged));
        this.invalidateInstallEstimationMetadataProfileCache = invalidateInstallEstimationMetadataProfileCache ?? throw new ArgumentNullException(nameof(invalidateInstallEstimationMetadataProfileCache));
        this.createFullOwnedResourceMaintenanceTargetSet = createFullOwnedResourceMaintenanceTargetSet ?? throw new ArgumentNullException(nameof(createFullOwnedResourceMaintenanceTargetSet));
        this.logStartupMemoryCheckpoint = logStartupMemoryCheckpoint ?? throw new ArgumentNullException(nameof(logStartupMemoryCheckpoint));
        this.createChartFolderPathFromCharts = createChartFolderPathFromCharts ?? throw new ArgumentNullException(nameof(createChartFolderPathFromCharts));
        this.getDuplicateInstallRepairPaths = getDuplicateInstallRepairPaths ?? throw new ArgumentNullException(nameof(getDuplicateInstallRepairPaths));
        this.applyMergeFolderMaintenanceAfterRelease = applyMergeFolderMaintenanceAfterRelease
            ?? throw new ArgumentNullException(nameof(applyMergeFolderMaintenanceAfterRelease));
        this.logReverseLookupMutationAndQueueWarmupIfNeeded = logReverseLookupMutationAndQueueWarmupIfNeeded ?? throw new ArgumentNullException(nameof(logReverseLookupMutationAndQueueWarmupIfNeeded));
        this.logInstallPerformance = logInstallPerformance ?? throw new ArgumentNullException(nameof(logInstallPerformance));
        this.logInstallPerformanceWarning = logInstallPerformanceWarning ?? throw new ArgumentNullException(nameof(logInstallPerformanceWarning));
        this.logFileInfo = logFileInfo ?? throw new ArgumentNullException(nameof(logFileInfo));
        this.logFileWarning = logFileWarning ?? throw new ArgumentNullException(nameof(logFileWarning));
        this.targetOnlyFileMutationOptions = targetOnlyFileMutationOptions ?? throw new ArgumentNullException(nameof(targetOnlyFileMutationOptions));
        this.recursiveDirectoryTreeFileMutationOptions = recursiveDirectoryTreeFileMutationOptions ?? throw new ArgumentNullException(nameof(recursiveDirectoryTreeFileMutationOptions));
        autoRenameBatchCoordinator = new(this);
    }

    internal bool IsLibraryRootFolder(string folderPath)
    {
        return GetLibraryDirectories().Contains(folderPath, StringComparer.OrdinalIgnoreCase);
    }

    internal string NormalizeAutoRenameFolderName(string folderName)
    {
        folderName ??= string.Empty;
        BmsLibraryOptionsSnapshot options = lr2SynchronizationOwner.CurrentOptionsSnapshot;
        if (options.UseOnlyShiftJISChars)
        {
            folderName = folderName.ToSjisSchemeString();
        }
        return folderName.RemoveInvalidFileNameChars();
    }

    internal bool DirectoryExists(string folderPath)
    {
        return LongPathFileSystem.DirectoryExists(folderPath);
    }

    internal bool EntryExists(string path)
    {
        return LongPathFileSystem.EntryExists(path);
    }

    private LibraryFileMutationLease EnterFolderMoveWriteScope()
    {
        return synchronization.EnterFolderMoveWriteScope();
    }

    private IDisposable EnterFolderMoveReadScope()
    {
        return synchronization.EnterFolderMoveReadScope();
    }

    private LibraryFileMutationLease EnterNormalInvalidExtensionRenameWriteScope()
    {
        return synchronization.EnterNormalInvalidExtensionRenameWriteScope();
    }

    private IDisposable EnterNormalInvalidExtensionRenameSnapshotScope()
    {
        return synchronization.EnterNormalInvalidExtensionRenameSnapshotScope();
    }

    private LibraryFileMutationLease EnterPendingInvalidExtensionRenameWriteScope()
    {
        return synchronization.EnterPendingInvalidExtensionRenameWriteScope();
    }

    private IDisposable EnterPendingInvalidExtensionRenameSnapshotScope()
    {
        return synchronization.EnterPendingInvalidExtensionRenameSnapshotScope();
    }

    private LibraryFileMutationLease EnterLibraryChartRemovalWriteScope()
    {
        return synchronization.EnterLibraryChartRemovalWriteScope();
    }

    private IDisposable EnterLibraryChartRemovalSnapshotScope()
    {
        return synchronization.EnterLibraryChartRemovalSnapshotScope();
    }

    private LibraryFileMutationLease EnterFixInstallationDirectoryWriteScope()
    {
        return synchronization.EnterFixInstallationDirectoryWriteScope();
    }

    private IDisposable EnterFixInstallationDirectorySnapshotScope()
    {
        return synchronization.EnterFixInstallationDirectorySnapshotScope();
    }

    private LibraryFileMutationLease EnterMergeWriteScope()
    {
        return synchronization.EnterMergeWriteScope();
    }

    internal void RunWithMergeSnapshotLocks(Action action)
    {
        using IDisposable snapshotScope = synchronization.EnterMergeSnapshotScope();
        action();
    }

    private bool TryBlockCatalogMutation(string operation, bool showMessage)
    {
        return synchronization.TryBlockCatalogMutation(operation, showMessage);
    }

    /// <summary>
    /// installed lookup と導入見積もり metadata を同じ currentness 境界で失効させます。
    /// </summary>
    internal void InvalidateInstalledDirectoryIndex()
    {
        InvalidateInstallEstimationMetadataProfileCache();
        lock (pendingInstallEstimateCurrentnessGate)
        {
            catalogOwnedCollectionOwner.InvalidateInstalledChartLookup();
        }
    }

    /// <summary>
    /// Runs a folder mutation with an explicit capability issued by its outer
    /// lease.  Nested catalog/file apply must use this overload; no ambient
    /// authorization is available.
    /// </summary>
    internal void RunWithFolderMoveWriteLocks(
        Action<LibraryFileMutationCapability> action)
    {
        using LibraryFileMutationLease mutationLease = EnterFolderMoveWriteScope();
        if (mutationLease == null)
        {
            return;
        }
        using LibraryFileMutationCapability capability = mutationLease.CreateMutationCapability();
        capability.Validate(lr2SynchronizationOwner);
        action(capability);
    }

    /// <summary>
    /// Captures the minimum chart/package model snapshot needed to build a
    /// filesystem mutation plan.  The scope is deliberately short-lived and
    /// must be disposed before executing the plan.
    /// </summary>
    internal void RunWithFolderMoveSnapshotLocks(Action action)
    {
        using IDisposable snapshotScope = synchronization.EnterFolderMoveSnapshotScope();
        action();
    }

    internal void RunWithFolderMoveReadLocks(Action action)
    {
        using IDisposable mutationScope = EnterFolderMoveReadScope();
        action();
    }

    internal void RunWithNormalInvalidExtensionRenameWriteLocks(
        Action<LibraryFileMutationCapability> action)
    {
        using (LibraryFileMutationLease mutationLease = EnterNormalInvalidExtensionRenameWriteScope())
        {
            if (mutationLease == null)
            {
                return;
            }
            using LibraryFileMutationCapability capability = mutationLease.CreateMutationCapability();
            capability.Validate(lr2SynchronizationOwner);
            action(capability);
        }
    }

    internal List<LibraryFileOperationTargetSnapshot> CaptureNormalInvalidExtensionRenameTargets(
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        using IDisposable snapshotScope = EnterNormalInvalidExtensionRenameSnapshotScope();
        return CaptureChartOperationTargetSnapshots(
            charts,
            chart => chart?.GetBmsStorageOwner() != null,
            captureSourceFileExistence: true);
    }

    internal void RunWithPendingInvalidExtensionRenameWriteLocks(
        Action<LibraryFileMutationCapability> action)
    {
        using (LibraryFileMutationLease mutationLease = EnterPendingInvalidExtensionRenameWriteScope())
        {
            if (mutationLease == null)
            {
                throw new InvalidOperationException(Resources.Warn_LibraryOperationBusy);
            }
            using LibraryFileMutationCapability capability = mutationLease.CreateMutationCapability();
            capability.Validate(lr2SynchronizationOwner);
            action(capability);
        }
    }

    private void RunWithLibraryChartRemovalWriteLocks(
        Action<LibraryFileMutationCapability> action)
    {
        using (LibraryFileMutationLease mutationLease = EnterLibraryChartRemovalWriteScope())
        {
            if (mutationLease == null)
            {
                return;
            }
            using LibraryFileMutationCapability capability = mutationLease.CreateMutationCapability();
            capability.Validate(lr2SynchronizationOwner);
            action(capability);
        }
    }

    internal List<LibraryChartRef> CreateNonNullChartRefList(IEnumerable<LibraryChartRef> charts)
    {
        return [.. (charts ?? []).Where(chart => chart != null)];
    }

    internal List<FolderAutoRenamePlan> BuildRootFolderMovePlans(
        IEnumerable<ChartFile> charts,
        string destinationDirectory)
    {
        return libraryFileOperationsService.BuildRootFolderMovePlans(
            ToLibraryChartRefs(charts),
            destinationDirectory);
    }

    /// <summary>現在のownerとpackage参照からfolder移動factsを捕捉します。</summary>
    /// <param name="sourceDirectory">移動元folder。</param>
    /// <param name="destinationDirectory">移動先folder。</param>
    /// <param name="unregister">catalogから登録解除するかどうか。</param>
    /// <param name="notifyStorageRowPathChanges">storage row path通知を要求するかどうか。</param>
    /// <returns>catalog/package factsと通知方針。</returns>
    internal LibraryFolderMoveFacts BuildFolderMoveFacts(
        string sourceDirectory,
        string destinationDirectory,
        bool unregister,
        bool notifyStorageRowPathChanges)
    {
        return libraryFileOperationsService.BuildFolderMoveFacts(
            sourceDirectory,
            destinationDirectory,
            ToLibraryChartRefs(CreateOwnedRealPathChartSnapshotsUnsafe(sourceDirectory)),
            CreateInstallDestinationOverlayChartRefSnapshot(),
            packageLifecycleOwner.PendingPackages,
            packageLifecycleOwner.InstalledPackages,
            unregister,
            notifyStorageRowPathChanges);
    }

    private LibraryChartRemovalPreflight CaptureLibraryChartRemovalPreflight(
        IReadOnlyList<LibraryChartRef> requestedCharts)
    {
        using IDisposable snapshotScope = EnterLibraryChartRemovalSnapshotScope();
        ILibraryChartCanonicalLookup lookup = CreateOwnedCanonicalChartLookupUnsafe();
        CanonicalChartResolveResult resolveResult = lookup.ResolveCanonicalCharts(
            (requestedCharts ?? []).Where(chart => !string.IsNullOrWhiteSpace(chart?.Path)));
        List<LibraryFileOperationTargetSnapshot> targets = [.. resolveResult.CanonicalCharts
            .Where(chart => !string.IsNullOrWhiteSpace(chart?.Path))
            .Select(chart => LibraryFileOperationTargetSnapshot.FromChart(chart?.ToChartFile(), captureSourceFileExistence: true))
            .Where(target => target != null)];
        return new LibraryChartRemovalPreflight
        {
            Targets = targets,
            WholeFolderCandidatePaths = libraryFileOperationsService.GetWholeFolderDeleteCandidatePaths(
                requestedCharts,
                lookup),
            UnresolvedPaths = [.. resolveResult.UnresolvedCharts
                .Select(chart => chart?.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))]
        };
    }

    private LibraryCatalogMutationFacts ExecuteLibraryChartRemovalAfterAdmission(
        IReadOnlyList<LibraryChartRef> requestedCharts,
        bool sendToRecycleBin,
        IReadOnlyList<string> approvedWholeFolderDeletePaths,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> preflightTargets,
        IReadOnlyList<string> preflightUnresolvedPaths,
        LibraryFileMutationCapability mutationCapability,
        out LibraryPackageReferenceFacts packageReferenceFacts,
        out List<LibraryDeleteFailure> failures,
        out LibraryChartRemovalOutcome filesystemOutcome,
        out int inputChartCount,
        out int canonicalChartCount,
        out int unresolvedChartCount,
        out int pathOnlyInputCount,
        out int removedChartCount,
        out int folderDeleteCount,
        out int fileDeleteCount,
        out IReadOnlyList<string> deletedFolderPaths)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        var validCanonicalCharts = new List<LibraryChartRef>();
        var validTargets = new List<LibraryFileOperationTargetSnapshot>();
        var unresolved = new List<LibraryDeleteFailure>();
        var targetFacts = new List<LibraryChartRemovalTarget>();
        LibraryChartRemovalPlan plan;
        List<LibraryChartRemovalInstallDestinationBinding> installDestinationBindings;
        CanonicalChartResolveResult currentResolveResult;
        int staleTargetCount = 0;
        using (EnterLibraryChartRemovalSnapshotScope())
        {
            ILibraryChartCanonicalLookup lookup = CreateOwnedCanonicalChartLookupUnsafe();
            currentResolveResult = lookup.ResolveCanonicalCharts(
                (requestedCharts ?? []).Where(chart => !string.IsNullOrWhiteSpace(chart?.Path)));
            inputChartCount = currentResolveResult.InputCount;
            pathOnlyInputCount = currentResolveResult.PathOnlyInputCount;
            canonicalChartCount = currentResolveResult.CanonicalCharts.Count(chart => !string.IsNullOrWhiteSpace(chart?.Path));
            unresolvedChartCount = currentResolveResult.UnresolvedCharts.Count;
            var usedPreflightTargets = new HashSet<LibraryFileOperationTargetSnapshot>();
            foreach (LibraryChartRef currentChart in currentResolveResult.CanonicalCharts.Where(chart => !string.IsNullOrWhiteSpace(chart?.Path)))
            {
                var currentTarget = LibraryFileOperationTargetSnapshot.FromChart(
                    currentChart.ToChartFile(),
                    captureSourceFileExistence: true);
                LibraryFileOperationTargetSnapshot expectedTarget = FindMatchingTargetSnapshot(
                    preflightTargets,
                    currentTarget,
                    usedPreflightTargets);
                bool identityChanged = preflightTargets != null
                    && (expectedTarget == null
                        || !LibraryFileOperationTargetSnapshot.HasSameIdentity(expectedTarget, currentTarget)
                        || expectedTarget.SourceFileExisted != currentTarget.SourceFileExisted);
                if (identityChanged || currentTarget == null)
                {
                    staleTargetCount++;
                    targetFacts.Add(new(currentChart.Path, LibraryChartRemovalState.Stale));
                    continue;
                }
                validCanonicalCharts.Add(currentChart);
                validTargets.Add(currentTarget);
            }
            staleTargetCount += Math.Max(0, (preflightTargets?.Count ?? 0) - usedPreflightTargets.Count);
            targetFacts.AddRange((preflightTargets ?? []).Where(target => !usedPreflightTargets.Contains(target))
                .Select(target => new LibraryChartRemovalTarget(target.SourcePath, LibraryChartRemovalState.Stale)));
            unresolved.AddRange((preflightUnresolvedPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new LibraryDeleteFailure
                {
                    Path = path,
                    Exception = new InvalidOperationException("Library chart could not be resolved from the current catalog."),
                    IsDirectory = false,
                    Reason = "resolve_failed"
                }));
            if (preflightTargets == null)
            {
                unresolved.AddRange(currentResolveResult.UnresolvedCharts
                    .Select(chart => chart?.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new LibraryDeleteFailure
                    {
                        Path = path,
                        Exception = new InvalidOperationException("Library chart could not be resolved from the current catalog."),
                        IsDirectory = false,
                        Reason = "resolve_failed"
                    }));
            }

            List<ChartPackage> pendingPackageSnapshots = ClonePendingPackageSnapshots(packageLifecycleOwner.PendingPackages);
            InstallDestinationOverlayChartRefSnapshot installDestinationOverlay = CreateInstallDestinationOverlayChartRefSnapshot();
            plan = libraryFileOperationsService.BuildLibraryChartRemovalPlan(
                validCanonicalCharts,
                lookup,
                installDestinationOverlay,
                pendingPackageSnapshots,
                approvedWholeFolderDeletePaths);
            installDestinationBindings = CaptureInstallDestinationBindings(
                plan.InstallDestinationTargets,
                packageLifecycleOwner.PendingPackages,
                installDestinationOverlay,
                validTargets);
        }

        LibraryChartRemovalExecutionResult execution = libraryFileOperationsService.ExecuteLibraryChartRemovalPlan(
            plan,
            sendToRecycleBin,
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions);
        targetFacts.AddRange(execution.Targets);
        targetFacts.AddRange(unresolved.Select(failure => new LibraryChartRemovalTarget(
            failure.Path, LibraryChartRemovalState.Unresolved, failure.Exception)));
        filesystemOutcome = new LibraryChartRemovalOutcome(targetFacts);
        var removalRequests = new List<OwnedChartRemoveRequest>();
        foreach (int targetIndex in execution.RemovedTargetIndexes.Distinct())
        {
            if (targetIndex < 0 || targetIndex >= validTargets.Count)
            {
                continue;
            }
            AddOwnerRemovalRequest(removalRequests, validTargets[targetIndex]);
        }
        foreach (LibraryDeleteFailure failure in unresolved)
        {
        }
        List<LibraryDeleteFailure> operationFailures = [.. unresolved, .. execution.Failures];
        var installDestinationChanges = new List<LibraryInstallDestinationChange>();
        HashSet<string> deletedFolders = new(execution.DeletedFolderPaths, StringComparer.OrdinalIgnoreCase);
        // Deleted folders use filesystem identity; the follow-up state targets are exact catalog rows.
        var clearedInstallTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibraryChartRemovalInstallDestinationTarget target in plan.InstallDestinationTargets ?? [])
        {
            if (target == null || !deletedFolders.Contains(target.FolderPath))
            {
                continue;
            }
            LibraryChartRemovalInstallDestinationBinding binding = installDestinationBindings
                .FirstOrDefault(candidate => IsSameInstallDestinationTarget(candidate, target));
            if (binding == null)
            {
                continue;
            }
            string targetKey = (target.IsPendingPackageEntry ? "pending:" : "library:")
                + target.Kind + ":" + target.Path;
            if (!clearedInstallTargets.Add(targetKey))
            {
                continue;
            }
            if (binding.Entry != null)
            {
                installDestinationChanges.Add(new LibraryInstallDestinationChange
                {
                    Entry = binding.Entry,
                    Chart = binding.Entry.Chart,
                    ClearInstallDestinationState = true
                });
            }
            else if (binding.Chart != null)
            {
                installDestinationChanges.Add(new LibraryInstallDestinationChange
                {
                    Chart = binding.Chart,
                    ClearInstallDestinationState = true
                });
            }
        }
        // Only successful filesystem subtrees are forwarded to the operation session.
        // The resource index itself must not change before the catalog durable point.
        deletedFolderPaths = Array.AsReadOnly((execution.DeletedFolderPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
        failures = operationFailures;
        packageReferenceFacts = new LibraryPackageReferenceFacts(installDestinationChanges, []);
        removedChartCount = execution.RemovedTargetIndexes.Distinct().Count();
        folderDeleteCount = execution.FolderDeleteCount;
        fileDeleteCount = execution.FileDeleteCount;
        return new LibraryCatalogMutationFacts(removalRequests, [], []);
    }

    private LibraryChartRemovalOutcome RemoveLibraryChartsCore(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> preflightTargets = null,
        IReadOnlyList<string> preflightUnresolvedPaths = null)
    {
        List<LibraryChartRef> requestedCharts = CreateNonNullChartRefList(charts);
        LibraryCatalogMutationFacts catalogFacts = ExecuteLibraryChartRemovalAfterAdmission(
            requestedCharts,
            sendToRecycleBin,
            [.. (approvedWholeFolderDeletePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            preflightTargets,
            preflightUnresolvedPaths,
            mutationCapability,
            out LibraryPackageReferenceFacts packageReferenceFacts,
            out List<LibraryDeleteFailure> failures,
            out LibraryChartRemovalOutcome filesystemOutcome,
            out int inputChartCount,
            out int canonicalChartCount,
            out int unresolvedChartCount,
            out int pathOnlyInputCount,
            out int removedChartCount,
            out int folderDeleteCount,
            out int fileDeleteCount,
            out IReadOnlyList<string> deletedFolderPaths);
        string resultLog = "delete_library_result input=" + inputChartCount
            + " canonical=" + canonicalChartCount
            + " unresolved=" + unresolvedChartCount
            + " pathOnly=" + pathOnlyInputCount
            + " removed=" + removedChartCount
            + " failures=" + failures.Count
            + " folderDeletes=" + folderDeleteCount
            + " fileDeletes=" + fileDeleteCount;
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        LibraryMutationSession session = BeginLibraryMutationSession(
            mutationCapability,
            "delete_library",
            postLeaseNotifications);
        session.AppendCatalogChange(
            catalogFacts,
            packageReferenceFacts,
            filesystemOutcome.Targets
                .Where(target => target.State == LibraryChartRemovalState.Confirmed)
                .Select(target => new LibraryMutationSessionTarget(target.Path, string.Empty)));
        session.AppendResourceDirectoryRemovals(deletedFolderPaths);
        LibraryMutationSessionReceipt sessionReceipt = session.Commit();
        postLeaseNotifications.Add(() => LogInstallPerformance(resultLog));
        var outcome = new LibraryChartRemovalOutcome(
            filesystemOutcome.Targets,
            sessionReceipt,
            catalogApplyAttempted: true);
        return outcome;
    }

    private void ShowDeleteFailure(LibraryDeleteFailure failure)
    {
        if (failure.IsDirectory)
        {
            ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else
        {
            ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
    }

    internal void LogReverseLookupMutationAndQueueWarmupIfNeeded(
        string reason,
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
    {
        logReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
    }

    internal void ShowCannotRenameRootFolder(string sourceDirectory)
    {
        ShowOperationDialog(
            string.Format(Resources.Warn_CannotRenameRootFolder, sourceDirectory),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal void ShowRenameFolderNotExists(string sourceDirectory)
    {
        ShowOperationDialog(
            string.Format(Resources.Warn_RenameFolderNotExists, sourceDirectory),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal void ShowMoveDestinationAlreadyExists(string sourceDirectory, string destinationDirectory)
    {
        ShowOperationDialog(
            string.Format(Resources.Warn_MoveDestAlreadyExists, sourceDirectory, destinationDirectory),
            Resources.MessageBoxTitle_Warning,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal void ShowMoveDestinationRootNotFound(string destinationDirectory)
    {
        ShowOperationDialog(
            string.Format(Resources.Error_MoveDestRootNotFound, destinationDirectory),
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    internal void ShowDriveRootCannotChangeRoot()
    {
        ShowOperationDialog(
            Resources.Warn_DriveRootCannotChangeRoot,
            Resources.MessageBoxTitle_Confirm,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal void ShowFolderMoveFailed(string sourceDirectory, string destinationDirectory, Exception exception)
    {
        ShowOperationDialog(
            string.Format(
                Resources.Error_FolderMoveFailed,
                sourceDirectory,
                destinationDirectory,
                DisplayedExceptionMessage.Format(exception)),
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    internal List<FolderAutoRenamePlan> BuildAutoRenamePlans(
        IEnumerable<ChartFile> selectedCharts,
        IEnumerable<string> rootFolders,
        bool renameRootFolder)
    {
        return libraryFileOperationsService.BuildAutoRenamePlans(
            selectedCharts,
            rootFolders,
            renameRootFolder,
            CreateDirectLibraryChartSnapshotsInFolders,
            createChartFolderPathFromCharts,
            NormalizeAutoRenameFolderName);
    }

    internal List<FolderAutoRenamePlan> BuildAutoRenamePlansForSourceFolders(
        string parentDirectory)
    {
        return libraryFileOperationsService.BuildAutoRenamePlansForSourceFolders(
            CreateOwnedRealPathChartDirectoriesUnsafe(parentDirectory),
            GetLibraryDirectories(),
            renameRootFolder: false,
            CreateDirectLibraryChartSnapshotsInFolders,
            createChartFolderPathFromCharts,
            NormalizeAutoRenameFolderName);
    }

    /// <summary>
    /// 計画済み folder rename を一つの operation-scoped session へ集約し、terminal facts を返します。
    /// LR2 synchronization は caller が同じ外側 lease 内で一回だけ完了します。
    /// </summary>
    /// <param name="plans">現在の command が所有する事前計算済み rename plans。</param>
    /// <param name="mutationCapability">外側のfolder mutation leaseが保持するlive capability。</param>
    /// <param name="progressReporter">item 単位の optional progress reporter。</param>
    /// <param name="postLeaseNotifications">lease 解放後に一回公開する command-owned notifications。</param>
    /// <returns>session receipt、LR2 finalization inputs、診断を保持する immutable result。</returns>
    internal AutoRenameBatchResult ApplyAutoRenamePlansWithSessionReceipt(
        IEnumerable<FolderAutoRenamePlan> plans,
        LibraryFileMutationCapability mutationCapability,
        Action<int, int, string> progressReporter,
        ICollection<Action> postLeaseNotifications)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        return autoRenameBatchCoordinator.ApplyWithSessionReceipt(
            plans,
            mutationCapability,
            postLeaseNotifications,
            progressReporter);
    }

    internal InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot()
    {
        return installDestinationStateOwner.CreateOverlaySnapshot(out _);
    }

    /// <summary>指定済みoverlayを使ってfolder移動factsを捕捉します。</summary>
    /// <param name="sourceDirectory">移動元folder。</param>
    /// <param name="destinationDirectory">移動先folder。</param>
    /// <param name="installDestinationOverlayCharts">snapshot済みinstall destination overlay。</param>
    /// <returns>catalog/package factsと通知方針。</returns>
    internal LibraryFolderMoveFacts BuildFolderMoveFacts(
        string sourceDirectory,
        string destinationDirectory,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts)
    {
        return libraryFileOperationsService.BuildFolderMoveFacts(
            sourceDirectory,
            destinationDirectory,
            ToLibraryChartRefs(CreateOwnedRealPathChartSnapshotsUnsafe(sourceDirectory)),
            installDestinationOverlayCharts,
            packageLifecycleOwner.PendingPackages,
            packageLifecycleOwner.InstalledPackages,
            unregister: false,
            notifyStorageRowPathChanges: false);
    }

    private List<ChartFile> CreateOwnedRealPathChartSnapshotsUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionReady();
        lock (catalogOwnedCollectionOwner.Gate)
        {
            return [.. catalogOwnedCollectionOwner.Collection.CreateLibraryChartRefsUnderRealPath(directoryPath)
            .Select(chart => chart?.ToChartFile())
            .Where(chart => chart != null)];
        }
    }

    private List<string> CreateOwnedRealPathChartDirectoriesUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionReady();
        lock (catalogOwnedCollectionOwner.Gate)
        {
            return catalogOwnedCollectionOwner.Collection.CreateChartDirectoriesUnderRealPath(directoryPath);
        }
    }

    private List<ChartFile> CreateOwnedStorageTargetChartSnapshotsForSubtreeDirectoryUnsafe(string directoryPath)
    {
        EnsureOwnedChartCollectionReady();
        ChartStorageTargetSet targets;
        lock (catalogOwnedCollectionOwner.Gate)
        {
            targets = catalogOwnedCollectionOwner.Collection.CreateStorageTargetsForSubtreeDirectory(directoryPath);
        }
        return [.. targets.BmsFiles
            .Select(file => ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false))
            .Concat(targets.BmsonSongs.Select(song => ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false)))
            .Where(chart => chart != null)];
    }

    private List<ChartFile> CreateDirectLibraryChartSnapshotsInFolders(IEnumerable<string> folderPaths)
    {
        EnsureOwnedChartCollectionReady();
        lock (catalogOwnedCollectionOwner.Gate)
        {
            return catalogOwnedCollectionOwner.Collection.CreateSnapshotForDirectChildDirectories(
                folderPaths ?? [],
                includeWarningSnapshot: true,
                includeResourceReferences: false,
                includeScoreSnapshot: false);
        }
    }

    private ILibraryChartCanonicalLookup CreateOwnedCanonicalChartLookupUnsafe()
    {
        EnsureOwnedChartCollectionReady();
        lock (catalogOwnedCollectionOwner.Gate)
        {
            return catalogOwnedCollectionOwner.Collection.CreateCanonicalChartLookupSnapshot();
        }
    }

    private List<string> GetLibraryDirectories()
    {
        return [.. lr2SynchronizationOwner.CaptureBmsDirectories().Roots];
    }

    private void EnsureOwnedChartCollectionReady()
    {
        catalogOwnedCollectionOwner.EnsureCurrent(catalogStorageRowsOwner);
    }

    private IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
        IEnumerable<ChartFile> excluded,
        string reason = null,
        long operationId = 0L)
    {
        return catalogOwnedCollectionOwner.CreateInstalledChartKeySnapshotExcludingCharts(
            excluded,
            catalogStorageRowsOwner,
            reason,
            operationId,
            LogInstallPerformance);
    }

    private IInstalledChartLookupIndex CreateInstalledChartLookupSnapshotUnsafe()
    {
        lock (pendingInstallEstimateCurrentnessGate)
        {
            return catalogOwnedCollectionOwner
                .CreateInstalledChartLookupVersionedSnapshot(
                    catalogStorageRowsOwner,
                    LogInstallPerformance)
                .Snapshot;
        }
    }

    private PendingFileDeletionResult DeletePendingCharts(
        IEnumerable<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms)
    {
        return packageInstallService.DeletePendingCharts(
            charts,
            packageLifecycleOwner.PendingPackages,
            sendToRecycleBin,
            deleteContainingPackageFoldersWhenNoBms,
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions);
    }

    private List<LibraryFileOperationTargetSnapshot> CaptureFixInstallationTargets(
        IEnumerable<ChartFile> charts)
    {
        using IDisposable snapshotScope = EnterFixInstallationDirectorySnapshotScope();
        return CaptureChartOperationTargetSnapshots(
            charts,
            chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination),
            captureSourceFileExistence: true);
    }

    private static ChartFile CreateDetachedFixChart(LibraryFileOperationTargetSnapshot target)
    {
        if (target?.BmsOwner != null)
        {
            BMSFile detachedOwner = target.BmsOwner.CreateSongRowPersistenceCopy();
            ChartFile detachedChart = ChartFileProjection.FromBmsFile(
                detachedOwner,
                includeWarningSnapshot: true,
                includeResourceReferences: false);
            return ChartFileProjection.WithPackageState(
                detachedChart,
                target.ChartSnapshot?.InstallDestination,
                target.ChartSnapshot?.InstallDestinationTitle,
                target.ChartSnapshot?.InstallDestinationArtist,
                target.ChartSnapshot?.InstallDestinationSuggestions ?? [],
                target.ChartSnapshot?.Warnings ?? []);
        }

        if (target?.BmsonOwner != null)
        {
            LR2SongDBExtended.bmson_song source = target.BmsonOwner;
            var detachedOwner = new LR2SongDBExtended.bmson_song
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
                HasFreshResourceReferences = source.HasFreshResourceReferences
            };
            ChartFile detachedChart = ChartFileProjection.FromBmsonSong(
                detachedOwner,
                includeWarningSnapshot: true,
                includeResourceReferences: false);
            return ChartFileProjection.WithPackageState(
                detachedChart,
                target.ChartSnapshot?.InstallDestination,
                target.ChartSnapshot?.InstallDestinationTitle,
                target.ChartSnapshot?.InstallDestinationArtist,
                target.ChartSnapshot?.InstallDestinationSuggestions ?? [],
                target.ChartSnapshot?.Warnings ?? []);
        }

        return target?.ChartSnapshot;
    }

    private LibraryFixInstallationResult FixInstallationDirectoryAfterAdmission(
        IReadOnlyList<ChartFile> requestedCharts,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> preflightTargets,
        IReadOnlyList<string> approvedDuplicateRemovalChartPaths,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        var result = new LibraryFixInstallationResult
        {
            RequestedCount = requestedCharts?.Count ?? 0
        };
        var mappings = new List<DetachedFixTarget>();
        List<ChartFile> detachedCharts;
        IPrimaryHashLookup existingHashes;
        using (EnterFixInstallationDirectorySnapshotScope())
        {
            for (int index = 0; index < (requestedCharts?.Count ?? 0); index++)
            {
                ChartFile requestedChart = requestedCharts[index];
                LibraryFileOperationTargetSnapshot expectedTarget = index < (preflightTargets?.Count ?? 0)
                    ? preflightTargets[index]
                    : null;
                bool identityChanged = expectedTarget == null
                    || !expectedTarget.HasSameLiveIdentity(requestedChart)
                    || !expectedTarget.SourceFileExisted
                    || !expectedTarget.SourceFileSafetyFactsAvailable
                    || expectedTarget.SourceFileIsReparsePoint
                    || !string.Equals(
                        expectedTarget.ChartSnapshot?.InstallDestination ?? string.Empty,
                        requestedChart?.InstallDestination ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase);
                Exception sourceFailure = null;
                if (identityChanged || !expectedTarget.TryValidateCurrentSource(out sourceFailure))
                {
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = expectedTarget?.SourcePath ?? requestedChart?.Path,
                        Exception = sourceFailure ?? new InvalidOperationException("Installation repair target became stale."),
                        IsDirectory = false,
                        Reason = "stale_target"
                    });
                    continue;
                }
                ChartFile detachedChart = CreateDetachedFixChart(expectedTarget);
                if (detachedChart == null)
                {
                    result.Failures.Add(new LibraryDeleteFailure
                    {
                        Path = expectedTarget.SourcePath,
                        Exception = new InvalidOperationException("Installation repair target could not be detached."),
                        IsDirectory = false,
                        Reason = "stale_target"
                    });
                    continue;
                }
                mappings.Add(new DetachedFixTarget
                {
                    Original = expectedTarget,
                    DetachedChart = detachedChart
                });
            }
            for (int index = requestedCharts?.Count ?? 0; index < (preflightTargets?.Count ?? 0); index++)
            {
                LibraryFileOperationTargetSnapshot staleTarget = preflightTargets[index];
                result.Failures.Add(new LibraryDeleteFailure
                {
                    Path = staleTarget?.SourcePath,
                    Exception = new InvalidOperationException("Installation repair target became stale."),
                    IsDirectory = false,
                    Reason = "stale_target"
                });
            }
            detachedCharts = [.. mappings.Select(mapping => mapping.DetachedChart)];
            existingHashes = CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
                detachedCharts,
                "fix_installation_directory",
                0L);
        }

        LibraryMutationSession session = BeginLibraryMutationSession(
            mutationCapability,
            "fix_installation_directory",
            postLeaseNotifications);
        session.AppendItemFailures(result.Failures
            .Where(failure => failure?.Exception != null)
            .Select(failure => new LibraryMutationSessionItemFailure(
                new LibraryMutationSessionTarget(failure.Path, string.Empty),
                failure.Exception)));

        var operationHashes = new PrimaryHashGuardLookup(existingHashes);
        var successfulRepairTargets = new List<LibraryFileOperationTargetSnapshot>();
        var approvedDuplicateRemovals = new List<LibraryChartRef>();
        IReadOnlyList<LibraryMutationSessionTarget> CaptureUnprocessedTargets(int nextMappingIndex)
        {
            return
            [
                .. approvedDuplicateRemovals.Select(removal =>
                    new LibraryMutationSessionTarget(removal.Path, string.Empty)),
                .. mappings.Skip(nextMappingIndex).Select(remaining =>
                    new LibraryMutationSessionTarget(
                        remaining.Original?.SourcePath,
                        remaining.DetachedChart?.InstallDestination))
            ];
        }
        bool physicalStopped = false;
        var stopwatch = Stopwatch.StartNew();
        for (int mappingIndex = 0; mappingIndex < mappings.Count; mappingIndex++)
        {
            DetachedFixTarget mapping = mappings[mappingIndex];
            LibraryFileOperationTargetSnapshot originalTarget = mapping.Original;
            ChartFile ownerChart = CreateOwnerChartSnapshot(originalTarget);
            var entry = PackageChartEntry.FromChart(mapping.DetachedChart);
            if (ownerChart == null || entry == null)
            {
                Exception invalidTargetFailure = new InvalidOperationException(
                    "Installation repair target could not be materialized for physical processing.");
                result.Failures.Add(new LibraryDeleteFailure
                {
                    Path = originalTarget?.SourcePath,
                    Exception = invalidTargetFailure,
                    IsDirectory = false,
                    Reason = "stale_target"
                });
                session.AppendItemFailures([
                    new LibraryMutationSessionItemFailure(
                        new LibraryMutationSessionTarget(originalTarget?.SourcePath, mapping.DetachedChart?.InstallDestination),
                        invalidTargetFailure)
                ]);
                continue;
            }

            var package = ChartPackage.FromChartEntries([entry]);
            package.path = originalTarget.SourcePath;
            package.delete_parent = false;
            string lookupKey = ChartLookupKey.GetPrimaryHash(mapping.DetachedChart);
            bool duplicateBeforeMove = !string.IsNullOrWhiteSpace(lookupKey)
                && operationHashes.ContainsPrimaryHash(lookupKey);
            PackagePhysicalMoveResult physicalMove = packageInstallService.MovePackageFilesPhysicalWithReceipt(
                package,
                mapping.DetachedChart.InstallDestination,
                lr2SynchronizationOwner.CurrentOptionsSnapshot,
                createChartFolderPathFromCharts,
                DisplayedExceptionMessage.Format,
                fileMutationService,
                dialogService,
                targetOnlyFileMutationOptions,
                recursiveDirectoryTreeFileMutationOptions,
                LogInstallPerformance,
                sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents,
                showMessageBoxOnInstallFail: false,
                existingHashes: operationHashes,
                independentOwnershipLookup: null,
                validatePhysicalResult: installResult =>
                {
                    if (duplicateBeforeMove || installResult?.AddedCharts?.SingleOrDefault() != null)
                    {
                        return null;
                    }
                    return new InvalidOperationException(
                        "Installation repair did not produce a moved chart projection.");
                },
                enqueueDiagnosticEffect: postLeaseNotifications.Add);
            FileDbMutationReceipt physicalReceipt = physicalMove.PhysicalReceipt;
            session.AppendRecoveryCandidatePaths(physicalReceipt.RecoveryPaths);
            if (physicalReceipt.CleanupFailure != null)
            {
                session.RecordCleanupFailure(physicalReceipt.CleanupFailure);
            }
            if (!physicalReceipt.DurableCommit
                || physicalReceipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired
                || physicalReceipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
            {
                Exception physicalFailure = physicalReceipt.FinalizationFailure
                    ?? physicalReceipt.Failure
                    ?? new InvalidOperationException("Installation repair physical phase did not complete.");
                IReadOnlyList<LibraryMutationSessionTarget> remainingTargets =
                    CaptureUnprocessedTargets(mappingIndex + 1);
                string failedDestination = physicalReceipt.DestinationPaths.FirstOrDefault()
                    ?? mapping.DetachedChart.InstallDestination;
                session.RecordStoppedSuffix(
                    originalTarget.SourcePath,
                    failedDestination,
                    physicalFailure,
                    remainingTargets);
                physicalStopped = true;
                break;
            }

            if (duplicateBeforeMove)
            {
                result.DuplicateSkippedCount++;
                if (IsApprovedDuplicateRemoval(ownerChart, approvedDuplicateRemovalChartPaths))
                {
                    var removableChart = LibraryChartRef.FromChartFile(ownerChart);
                    if (removableChart != null)
                    {
                        approvedDuplicateRemovals.Add(removableChart);
                    }
                }
                continue;
            }

            ChartFile movedChart = physicalMove.ExecutionResult?.AddedCharts?.SingleOrDefault();
            if (movedChart == null)
            {
                Exception missingProjectionFailure = new InvalidOperationException(
                    "Installation repair physical phase lost its moved chart projection.");
                session.RecordStoppedSuffix(
                    originalTarget.SourcePath,
                    mapping.DetachedChart.InstallDestination,
                    missingProjectionFailure,
                    CaptureUnprocessedTargets(mappingIndex + 1));
                physicalStopped = true;
                break;
            }

            (LibraryCatalogMutationFacts CatalogFacts, LibraryPackageReferenceFacts PackageReferenceFacts) mutationFacts =
                CreateFixMutationFacts(ownerChart, originalTarget.SourcePath, movedChart.Path);
            session.AppendCatalogChange(
                mutationFacts.CatalogFacts,
                mutationFacts.PackageReferenceFacts,
                [new LibraryMutationSessionTarget(originalTarget.SourcePath, movedChart.Path)]);
            operationHashes.AddPrimaryHash(lookupKey);
            successfulRepairTargets.Add(originalTarget);
            result.MovedCount++;
        }

        if (!physicalStopped && approvedDuplicateRemovals.Count > 0)
        {
            LibraryCatalogMutationFacts removalFacts = ExecuteLibraryChartRemovalAfterAdmission(
                approvedDuplicateRemovals,
                sendToRecycleBin: true,
                approvedWholeFolderDeletePaths: [],
                preflightTargets: null,
                preflightUnresolvedPaths: null,
                mutationCapability: mutationCapability,
                out LibraryPackageReferenceFacts removalPackageFacts,
                out List<LibraryDeleteFailure> removalFailures,
                out LibraryChartRemovalOutcome filesystemOutcome,
                out int inputChartCount,
                out int canonicalChartCount,
                out int unresolvedChartCount,
                out int pathOnlyInputCount,
                out int removedChartCount,
                out int folderDeleteCount,
                out int fileDeleteCount,
                out IReadOnlyList<string> deletedFolderPaths);
            result.Failures.AddRange(removalFailures);
            result.ApprovedRemovedCount = filesystemOutcome.ConfirmedChartCount;
            session.AppendCatalogChange(
                removalFacts,
                removalPackageFacts,
                filesystemOutcome.Targets
                    .Where(target => target.State == LibraryChartRemovalState.Confirmed)
                    .Select(target => new LibraryMutationSessionTarget(target.Path, string.Empty)));
            session.AppendResourceDirectoryRemovals(deletedFolderPaths);
            session.AppendItemFailures(filesystemOutcome.Targets
                .Where(target => target.State != LibraryChartRemovalState.Confirmed)
                .Select(target => new LibraryMutationSessionItemFailure(
                    new LibraryMutationSessionTarget(target.Path, string.Empty),
                    target.Failure ?? new InvalidOperationException(
                        "Approved duplicate removal was not confirmed: " + target.State))));
            string removalResultLog = "fix_installation_directory_delete_result input=" + inputChartCount
                + " canonical=" + canonicalChartCount
                + " unresolved=" + unresolvedChartCount
                + " pathOnly=" + pathOnlyInputCount
                + " removed=" + removedChartCount
                + " failures=" + removalFailures.Count
                + " folderDeletes=" + folderDeleteCount
                + " fileDeletes=" + fileDeleteCount;
            postLeaseNotifications.Add(() => LogInstallPerformance(removalResultLog));
        }

        result.SessionReceipt = session.Commit();
        if (result.SessionReceipt.DurableCommit
            && result.SessionReceipt.ApplyFailure == null
            && result.SessionReceipt.FinalizationFailure == null
            && successfulRepairTargets.Count > 0)
        {
            var seenMaintenanceOwners = new HashSet<object>();
            List<ChartFile> maintenanceTargets = [];
            foreach (LibraryFileOperationTargetSnapshot target in successfulRepairTargets)
            {
                ChartFile maintenanceChart = CreateOwnerChartSnapshot(target);
                object owner = maintenanceChart?.GetBmsStorageOwner()
                    ?? (object)maintenanceChart?.GetBmsonStorageOwner()
                    ?? maintenanceChart;
                if (maintenanceChart != null && seenMaintenanceOwners.Add(owner))
                {
                    maintenanceTargets.Add(maintenanceChart);
                }
            }
            maintenanceTargets = NormalizeResourceMaintenanceTargetCharts(maintenanceTargets);
            if (maintenanceTargets.Count > 0)
            {
                Exception maintenanceFailure = null;
                try
                {
                    MaintenanceWorkflowResult maintenanceResult = ApplyCatalogMaintenanceUnderExistingReservation(
                        maintenanceTargets,
                        true,
                        "fix_installation_directory",
                        postLeaseNotifications.Add);
                    if (maintenanceResult.Canceled)
                    {
                        maintenanceFailure = new OperationCanceledException();
                    }
                }
                catch (Exception exception)
                {
                    maintenanceFailure = exception;
                }
                if (maintenanceFailure != null)
                {
                    result.SessionReceipt = result.SessionReceipt.WithFinalizationFailure(maintenanceFailure);
                }
            }
        }
        stopwatch.Stop();
        result.TotalMs = stopwatch.ElapsedMilliseconds;
        result.Failure = result.SessionReceipt?.PrimaryFailure;
        return result;
    }

    private static (LibraryCatalogMutationFacts CatalogFacts, LibraryPackageReferenceFacts PackageReferenceFacts) CreateFixMutationFacts(
        ChartFile ownerChart,
        string oldPath,
        string newPath)
    {
        LibraryChartPathChange pathChange = new()
        {
            Chart = ownerChart,
            OldPath = oldPath,
            NewPath = newPath
        };
        LibraryInstallDestinationChange installDestinationChange = new()
        {
            Chart = ownerChart,
            NewInstallDestination = null
        };
        return (
            new LibraryCatalogMutationFacts(chartPathChanges: [pathChange]),
            new LibraryPackageReferenceFacts([installDestinationChange], []));
    }

    private LibraryPackageReferenceFacts PrepareMergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<ChartFile> sourceCharts,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        string hashSnapshotReason,
        long operationId,
        out bool success,
        out List<ChartFile> preparedSourceCharts,
        out IPrimaryHashLookup existingHashes)
    {
        LibraryMergeResult sourceResult = libraryFileOperationsService.PrepareMergeDirectory(
            sourceDirectory,
            destinationDirectory,
            ToLibraryChartRefs(sourceCharts),
            installDestinationOverlayCharts,
            packageLifecycleOwner.PendingPackages,
            packageLifecycleOwner.InstalledPackages,
            excluded => (excluded ?? []).Any()
                ? CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, hashSnapshotReason, operationId)
                : EmptyPrimaryHashLookup.Instance);
        success = sourceResult.Success;
        preparedSourceCharts = [.. (sourceResult.SourceCharts ?? [])
            .Select(chart => chart?.ToChartFile())
            .Where(chart => chart != null)];
        existingHashes = sourceResult.ExistingHashes ?? EmptyPrimaryHashLookup.Instance;
        return sourceResult.ReferenceFacts;
    }

    private List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(chart => chart != null)];
    }

    /// <summary>
    /// 指定された chart だけを対象にする resource maintenance target を作成します。
    /// </summary>
    private ResourceMaintenanceTargetSet CreateResourceMaintenanceTargetSet(IEnumerable<ChartFile> charts)
    {
        return ResourceMaintenanceTargetSet.ForSubset(NormalizeResourceMaintenanceTargetCharts(charts));
    }

    private string CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDirectory)
    {
        return createChartFolderPathFromCharts(chartFiles, parentDirectory);
    }

    private IEnumerable<string> GetDuplicateInstallRepairPaths(ChartFile chart)
    {
        return getDuplicateInstallRepairPaths(chart);
    }

    private DirectoryResourceLookupCache.ReverseLookupMutationResult RemoveReverseLookupDirectoriesUnderSource(string sourceDirectory)
    {
        return resourceIndexOwner.RemoveUnderSourceDirectory(sourceDirectory).MutationResult;
    }

    private DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectories(ChartScanResult scan)
    {
        return resourceIndexOwner.AddScanDirectories(scan).MutationResult;
    }

    private void LogInstallPerformanceWarning(string message)
    {
        logInstallPerformanceWarning(message);
    }

    private UiDialogDefaultResult ShowOperationDialog(
        string message,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult)
    {
        return dialogService.Show(message, caption, button, icon, defaultResult);
    }

    private bool IsApprovedDuplicateRemoval(
        ChartFile chart,
        IEnumerable<string> approvedDuplicateRemovalChartPaths)
    {
        HashSet<string> approvedPaths = approvedDuplicateRemovalChartPaths == null
            ? null
            : new HashSet<string>(approvedDuplicateRemovalChartPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (approvedPaths != null)
        {
            return !string.IsNullOrWhiteSpace(chart?.Path) && approvedPaths.Contains(chart.Path);
        }
        return ShowOperationDialog(
            string.Format(Resources.Confirm_DuplicateReinstallSkipped, chart?.Path, string.Join(Environment.NewLine, GetDuplicateInstallRepairPaths(chart))),
            Resources.MessageBoxTitle_Confirm,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    private bool IsApprovedWholeFolderDelete(
        string folderPath,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        HashSet<string> approvedPaths = approvedWholeFolderDeletePaths == null
            ? null
            : new HashSet<string>(approvedWholeFolderDeletePaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        if (approvedPaths != null)
        {
            return approvedPaths.Contains(folderPath);
        }
        return ShowOperationDialog(
            string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderPath),
            Resources.MessageBoxTitle_Confirm,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }

    private static List<LibraryChartRef> ToLibraryChartRefs(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? [])
            .Select(LibraryChartRef.FromChartFile)
            .Where(chart => chart != null)];
    }

    private static void AddOwnerRemovalRequest(
        ICollection<OwnedChartRemoveRequest> removalRequests,
        LibraryFileOperationTargetSnapshot target)
    {
        if (removalRequests == null || target == null)
        {
            return;
        }
        OwnedChartRemoveRequest request = target.BmsOwner != null
            ? OwnedChartRemoveRequest.FromOwnerReference(target.BmsOwner, target.ChartSnapshot)
            : OwnedChartRemoveRequest.FromOwnerReference(target.BmsonOwner, target.ChartSnapshot);
        if (request != null)
        {
            removalRequests.Add(request);
        }
    }

    private static ChartFile CreateOwnerChartSnapshot(LibraryFileOperationTargetSnapshot target)
    {
        if (target?.BmsOwner != null)
        {
            return ChartFileProjection.FromBmsStorageOwnerIdentity(target.BmsOwner);
        }
        if (target?.BmsonOwner != null)
        {
            return ChartFileProjection.FromBmsonStorageOwnerIdentity(target.BmsonOwner);
        }
        return target?.ChartSnapshot;
    }

    private static List<LibraryFileOperationTargetSnapshot> CaptureChartOperationTargetSnapshots(
        IEnumerable<ChartFile> charts,
        Func<ChartFile, bool> predicate,
        bool captureSourceFileExistence)
    {
        return [.. (charts ?? [])
            .Where(chart => predicate?.Invoke(chart) != false)
            .Select(chart => LibraryFileOperationTargetSnapshot.FromChart(chart, captureSourceFileExistence))
            .Where(target => target != null)];
    }

    private static LibraryFileOperationTargetSnapshot FindMatchingTargetSnapshot(
        IReadOnlyList<LibraryFileOperationTargetSnapshot> expectedTargets,
        LibraryFileOperationTargetSnapshot currentTarget,
        ISet<LibraryFileOperationTargetSnapshot> usedTargets)
    {
        if (currentTarget == null)
        {
            return null;
        }
        foreach (LibraryFileOperationTargetSnapshot expectedTarget in expectedTargets ?? [])
        {
            if (expectedTarget == null || usedTargets?.Contains(expectedTarget) == true)
            {
                continue;
            }
            if (!LibraryFileOperationTargetSnapshot.HasSameIdentity(expectedTarget, currentTarget))
            {
                continue;
            }
            usedTargets?.Add(expectedTarget);
            return expectedTarget;
        }
        return null;
    }

    private static List<ChartPackage> ClonePendingPackageSnapshots(IEnumerable<ChartPackage> packages)
    {
        List<ChartPackage> snapshots = [];
        foreach (ChartPackage package in packages ?? [])
        {
            if (package == null)
            {
                continue;
            }
            List<PackageChartEntry> entries = [.. package.ChartEntries
                .Select(entry => entry?.Chart)
                .Select(ChartFileProjection.ToImmutableSnapshot)
                .Select(PackageChartEntry.FromChart)
                .Where(entry => entry?.Chart != null)];
            var snapshot = ChartPackage.FromChartEntries(entries);
            snapshot.path = package.path;
            snapshot.delete_parent = package.delete_parent;
            snapshots.Add(snapshot);
        }
        return snapshots;
    }

    private static List<LibraryChartRemovalInstallDestinationBinding> CaptureInstallDestinationBindings(
        IReadOnlyList<LibraryChartRemovalInstallDestinationTarget> targets,
        IEnumerable<ChartPackage> pendingPackages,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlay,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> liveLibraryTargets)
    {
        var bindings = new List<LibraryChartRemovalInstallDestinationBinding>();
        foreach (LibraryChartRemovalInstallDestinationTarget target in targets ?? [])
        {
            if (target == null)
            {
                continue;
            }
            if (target.IsPendingPackageEntry)
            {
                foreach (PackageChartEntry entry in (pendingPackages ?? [])
                    .Where(package => package != null)
                    .SelectMany(package => package.ChartEntries ?? [])
                    .Where(entry => IsSameInstallDestinationTarget(entry?.Chart, target)))
                {
                    ChartFile chart = entry.Chart;
                    bindings.Add(new LibraryChartRemovalInstallDestinationBinding
                    {
                        FolderPath = target.FolderPath,
                        Kind = target.Kind,
                        Path = target.Path,
                        Md5 = target.Md5,
                        Sha256 = target.Sha256,
                        Entry = entry,
                        Chart = chart
                    });
                }
                continue;
            }

            foreach (LibraryChartRef chartRef in (installDestinationOverlay ?? InstallDestinationOverlayChartRefSnapshot.Empty)
                .GetChartRefsUnderInstallDestination(target.FolderPath)
                .Where(chart => IsSameInstallDestinationTarget(chart?.ToChartFile(), target)))
            {
                var chart = chartRef?.ToChartFile();
                LibraryFileOperationTargetSnapshot liveTarget = (liveLibraryTargets ?? [])
                    .FirstOrDefault(candidate => IsSameInstallDestinationTarget(candidate?.ChartSnapshot, target));
                bindings.Add(new LibraryChartRemovalInstallDestinationBinding
                {
                    FolderPath = target.FolderPath,
                    Kind = target.Kind,
                    Path = target.Path,
                    Md5 = target.Md5,
                    Sha256 = target.Sha256,
                    Chart = liveTarget == null ? chart : CreateOwnerChartSnapshot(liveTarget)
                });
            }
        }
        return bindings;
    }

    private static bool IsSameInstallDestinationTarget(
        LibraryChartRemovalInstallDestinationBinding binding,
        LibraryChartRemovalInstallDestinationTarget target)
    {
        return binding != null
            && target != null
            && binding.Kind == target.Kind
            && string.Equals(binding.Path, target.Path, StringComparison.Ordinal)
            && string.Equals(binding.Md5 ?? string.Empty, target.Md5 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(binding.Sha256 ?? string.Empty, target.Sha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameInstallDestinationTarget(
        ChartFile chart,
        LibraryChartRemovalInstallDestinationTarget target)
    {
        return chart != null
            && target != null
            && (chart.Kind == ChartFileKind.Bmson ? LibraryChartKind.Bmson : LibraryChartKind.Bms) == target.Kind
            && string.Equals(chart.Path, target.Path, StringComparison.Ordinal)
            && string.Equals(chart.Md5 ?? string.Empty, target.Md5 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && string.Equals(chart.Sha256 ?? string.Empty, target.Sha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    internal MovedFolderReferenceUpdateResult UpdateMovedFolderReferences(
        List<LibraryFolderPathChange> movedFolders)
    {
        LibraryResourceIndexMovedFoldersResult result =
            resourceIndexOwner.UpdateMovedFolderReferences(movedFolders);
        return new MovedFolderReferenceUpdateResult
        {
            MutationResult = result.Receipt.MutationResult,
            MoveCount = result.MoveCount,
            LookupKeyCount = result.LookupKeyCount,
            MatchedKeyCount = result.MatchedKeyCount,
            ElapsedMs = result.ElapsedMs
        };
    }

    /// <summary>
    /// Builds and executes the normal legacy extension-rename plan under the
    /// already-admitted lease.  Only a short model snapshot is held while the
    /// plan is built; the path executor receives no live storage references.
    /// </summary>
    internal LibraryFileExtensionRenameResult RenameLibraryFileExtensionsAfterAdmission(
        IEnumerable<ChartFile> targetCharts,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> preflightTargets,
        string newExt,
        bool unregister)
    {
        List<LibraryFileOperationTargetSnapshot> currentTargets;
        using (EnterNormalInvalidExtensionRenameSnapshotScope())
        {
            currentTargets = CaptureChartOperationTargetSnapshots(
                targetCharts,
                chart => chart?.GetBmsStorageOwner() != null,
                captureSourceFileExistence: true);
        }

        var validTargets = new List<LibraryFileOperationTargetSnapshot>();
        int staleTargetCount = 0;
        for (int index = 0; index < currentTargets.Count; index++)
        {
            LibraryFileOperationTargetSnapshot current = currentTargets[index];
            LibraryFileOperationTargetSnapshot expected = index < (preflightTargets?.Count ?? 0)
                ? preflightTargets[index]
                : null;
            if (!LibraryFileOperationTargetSnapshot.HasSameIdentity(expected, current)
                || !current.SourceFileExisted)
            {
                staleTargetCount++;
                continue;
            }
            validTargets.Add(current);
        }
        staleTargetCount += Math.Max(0, (preflightTargets?.Count ?? 0) - currentTargets.Count);

        LegacyInvalidExtensionRenamePlan plan = libraryFileOperationsService.BuildInvalidExtensionRenamePlan(validTargets, newExt);
        LegacyInvalidExtensionRenameExecutionResult execution = libraryFileOperationsService.ExecuteInvalidExtensionRenamePlan(
            plan,
            fileMutationService,
            targetOnlyFileMutationOptions,
            logFileInfo,
            logFileWarning);
        var removalRequests = new List<OwnedChartRemoveRequest>();
        var pathChanges = new List<LibraryChartPathChange>();
        var failures = new List<LibraryDeleteFailure>();
        var confirmedTargets = new List<LibraryMutationSessionTarget>();
        int executionIndex = 0;
        foreach (LibraryFileOperationTargetSnapshot target in validTargets)
        {
            if (!target.SourceFileExisted)
            {
                continue;
            }
            LegacyInvalidExtensionRenameExecutionItem executionItem = executionIndex < execution.Items.Count
                ? execution.Items[executionIndex++]
                : null;
            if (executionItem?.Outcome == null)
            {
                continue;
            }
            RenameInvalidExtensionOutcome outcome = executionItem.Outcome;
            switch (outcome.Action)
            {
                case RenameInvalidExtensionAction.Renamed:
                    confirmedTargets.Add(new LibraryMutationSessionTarget(
                        target.SourcePath,
                        outcome.FinalPath));
                    if (unregister)
                    {
                        AddOwnerRemovalRequest(removalRequests, target);
                    }
                    else
                    {
                        ChartFile ownerChart = CreateOwnerChartSnapshot(target);
                        if (ownerChart != null)
                        {
                            pathChanges.Add(new LibraryChartPathChange
                            {
                                Chart = ownerChart,
                                OldPath = target.SourcePath,
                                NewPath = outcome.FinalPath
                            });
                        }
                    }
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    confirmedTargets.Add(new LibraryMutationSessionTarget(target.SourcePath, string.Empty));
                    AddOwnerRemovalRequest(removalRequests, target);
                    break;
                default:
                    if (outcome.FailureException != null)
                    {
                        failures.Add(new LibraryDeleteFailure
                        {
                            Path = target.SourcePath,
                            Exception = outcome.FailureException,
                            IsDirectory = false
                        });
                    }
                    break;
            }
        }
        LibraryCatalogMutationFacts catalogFacts = new(removalRequests, pathChanges, []);
        LibraryFileExtensionRenameReport report = new(
            failures,
            execution.RenamedCount,
            execution.DuplicateDeletedCount,
            execution.SkippedCount + staleTargetCount,
            execution.TotalMs);
        return new LibraryFileExtensionRenameResult(catalogFacts, report, confirmedTargets);
    }

    /// <summary>
    /// Executes the pending legacy extension-rename plan after exclusive
    /// admission.  The selected chart references are authoritative for this
    /// operation; only target filesystem facts are rechecked by the executor.
    /// </summary>
    internal PendingExtensionRenameReport RenamePendingBmsFormatChartFileExtensionsAfterAdmission(
        IEnumerable<ChartFile> targetCharts,
        string newExt)
    {
        List<ChartFile> charts = [.. (targetCharts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        var result = new PendingExtensionRenameReport
        {
            Total = charts.Count
        };
        List<LibraryFileOperationTargetSnapshot> validTargets =
            [.. charts.Select(chart => LibraryFileOperationTargetSnapshot.FromChart(chart, captureSourceFileExistence: true))
                .Where(target => target != null)];

        LegacyInvalidExtensionRenameExecutionResult execution = libraryFileOperationsService.ExecuteInvalidExtensionRenamePlan(
            libraryFileOperationsService.BuildInvalidExtensionRenamePlan(validTargets, newExt, rejectUnsafeSource: true),
            fileMutationService,
            targetOnlyFileMutationOptions,
            logFileInfo,
            logFileWarning);
        result.Renamed += execution.RenamedCount;
        result.DuplicateDeleted += execution.DuplicateDeletedCount;
        result.Skipped += execution.SkippedCount;
        result.Failed += execution.FailedCount;
        result.TotalMs = execution.TotalMs;
        foreach (LegacyInvalidExtensionRenameExecutionItem item in execution.Items)
        {
            if (item?.Outcome == null)
            {
                continue;
            }
            if (item.Outcome.Action is RenameInvalidExtensionAction.Renamed or RenameInvalidExtensionAction.DeletedAsDuplicate)
            {
                result.ChartPathsToRemove.Add(item.PlanItem.SourcePath);
            }
            else if (item.Outcome.FailureException != null)
            {
                result.Failures.Add(new PendingExtensionRenameFailureReport
                {
                    FilePath = item.PlanItem.SourcePath,
                    Outcome = item.Outcome
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Executes the zero-note rename for the selected pending chart references
    /// after operation admission.  Filesystem target facts are checked at the
    /// point of mutation; package membership is owned by the admitted route.
    /// </summary>
    internal PendingZeroNoteRenameResult RenamePendingZeroNoteBmsFormatChartsAfterAdmission(
        IEnumerable<ChartFile> targetCharts,
        Func<BMSFile, string, RenameInvalidExtensionOutcome> processRename,
        CancellationToken token = default,
        Action onEachProcessed = null,
        Action<string> logInfo = null)
    {
        List<ChartFile> charts = [.. (targetCharts ?? []).Where(chart => chart?.GetBmsStorageOwner() != null)];
        PendingZeroNoteRenameResult execution = packageInstallService.RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions(
            charts,
            processRename,
            token,
            onEachProcessed,
            logInfo);
        execution.Total = charts.Count;
        return execution;
    }

    /// <summary>
    /// deletion lease を解放した後に観測した filesystem と catalog の事実を返します。
    /// </summary>
    internal LibraryChartRemovalOutcome RemoveLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        if (TryBlockCatalogMutation(nameof(BMSLibrary.RemoveLibraryCharts), showMessage: true))
        {
            return null;
        }
        List<LibraryChartRef> requestedCharts = CreateNonNullChartRefList(charts);
        LibraryChartRemovalPreflight preflight = CaptureLibraryChartRemovalPreflight(requestedCharts);
        List<string> approvedPaths = approvedWholeFolderDeletePaths == null
            ? []
            : [.. approvedWholeFolderDeletePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (approvedWholeFolderDeletePaths == null)
        {
            foreach (string candidatePath in preflight.WholeFolderCandidatePaths)
            {
                if (ShowOperationDialog(
                    string.Format(Resources.Confirm_DeleteFolderWithNoBms, candidatePath),
                    Resources.MessageBoxTitle_Confirm,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes) == MessageBoxResult.Yes)
                {
                    approvedPaths.Add(candidatePath);
                }
            }
        }
        List<Action> postLeaseNotifications = [];
        LibraryChartRemovalOutcome outcome = null;
        RunWithLibraryChartRemovalWriteLocks(
            mutationCapability => outcome = RemoveLibraryChartsCore(
                requestedCharts,
                sendToRecycleBin,
                approvedPaths,
                mutationCapability,
                postLeaseNotifications,
                preflight.Targets,
                preflight.UnresolvedPaths));
        InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        return outcome;
    }

    internal void RemovePendingCharts(
        IEnumerable<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        List<ChartFile> requestedCharts = [.. charts.Where(chart => chart != null)];
        List<Action> postLeaseNotifications = [];
        RunWithPendingInvalidExtensionRenameWriteLocks(mutationCapability =>
        {
            PendingFileDeletionResult result = DeletePendingCharts(
                requestedCharts,
                sendToRecycleBin,
                deleteContainingPackageFoldersWhenNoBms);
            List<Action> diagnosticEffects = [];
            foreach (PendingFileDeletionFailure failure in result.Failures)
            {
                if (failure?.Exception == null)
                {
                    continue;
                }
                if (failure.IsDirectory)
                {
                    diagnosticEffects.Add(() => ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                }
                else
                {
                    diagnosticEffects.Add(() => ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK));
                }
            }
            RemovePendingChartsFromPendingPackagesAndInstallRows(
                result.ChartPathsToRemove,
                mutationCapability,
                postLeaseNotifications);
            postLeaseNotifications.AddRange(diagnosticEffects);
        });
        InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
    }

    /// <summary>
    /// Repairs selected chart locations through one operation-scoped mutation session and
    /// retains movement, approved deletion, and terminal facts for the caller after the
    /// mutation lease is released.
    /// </summary>
    internal LibraryFixInstallationResult FixInstallationDirectoryCharts(
        IEnumerable<ChartFile> charts,
        IEnumerable<string> approvedDuplicateRemovalChartPaths)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (TryBlockCatalogMutation(nameof(BMSLibrary.FixInstallationDirectoryCharts), showMessage: true))
        {
            return null;
        }

        List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
        List<LibraryFileOperationTargetSnapshot> preflightTargets = CaptureFixInstallationTargets(chartList);
        List<string> approvedDuplicateRemovalPaths = approvedDuplicateRemovalChartPaths == null
            ? []
            : [.. approvedDuplicateRemovalChartPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (approvedDuplicateRemovalChartPaths == null)
        {
            foreach (LibraryFileOperationTargetSnapshot target in preflightTargets)
            {
                ChartFile chart = target.ChartSnapshot;
                List<string> duplicatePaths = [.. GetDuplicateInstallRepairPaths(chart)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                if (duplicatePaths.Count == 0)
                {
                    continue;
                }
                if (ShowOperationDialog(
                    string.Format(
                        Resources.Confirm_DuplicateReinstallSkipped,
                        chart.Path,
                        string.Join(Environment.NewLine, duplicatePaths)),
                    Resources.MessageBoxTitle_Confirm,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question,
                    MessageBoxResult.Yes) == MessageBoxResult.Yes)
                {
                    approvedDuplicateRemovalPaths.Add(chart.Path);
                }
            }
        }

        LibraryFixInstallationResult result = null;
        List<Action> postLeaseNotifications = [];
        try
        {
            using (LibraryFileMutationLease mutationLease = EnterFixInstallationDirectoryWriteScope())
            {
                if (mutationLease == null)
                {
                    return null;
                }
                using LibraryFileMutationCapability mutationCapability = mutationLease.CreateMutationCapability();
                mutationCapability.Validate(lr2SynchronizationOwner);
                result = FixInstallationDirectoryAfterAdmission(
                    chartList,
                    preflightTargets,
                    approvedDuplicateRemovalPaths,
                    mutationCapability,
                    postLeaseNotifications);
            }
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return result;
    }

    /// <summary>
    /// active な file mutation lease 内で pending package と install row の削除を適用し、
    /// collection 公開を caller の lease 解放後 effect list へ積みます。
    /// </summary>
    /// <param name="chartPaths">Chart source paths removed by the filesystem command.</param>
    /// <param name="mutationCapability">Capability issued by the active mutation lease.</param>
    /// <param name="postLeaseNotifications">Command-owned effects flushed after lease release.</param>
    internal void RemovePendingChartsFromPendingPackagesAndInstallRows(
        IEnumerable<string> chartPaths,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications)
    {
        if (mutationCapability == null)
        {
            throw new ArgumentNullException(nameof(mutationCapability));
        }
        if (postLeaseNotifications == null)
        {
            throw new ArgumentNullException(nameof(postLeaseNotifications));
        }
        List<string> paths = [.. (chartPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Count == 0)
        {
            return;
        }
        PendingPackageMutationDelta delta = packageInstallService.BuildPendingPackageMutationDelta(
            packageLifecycleOwner.PendingPackages,
            chartPathsToRemove: paths);

        // The install-row mutation is durable work owned by the active file
        // lease. Collection notifications are held in the package owner's
        // existing deferral scope and flushed after release, including when
        // the durable mutation fails.
        IDisposable collectionPublicationScope = packageLifecycleOwner.BeginCollectionMutationScope();
        postLeaseNotifications.Add(collectionPublicationScope.Dispose);
        packageLifecycleOwner.ApplyPendingPackageMutationDelta(delta);
    }

    internal void ShowNormalRenameFailure(LibraryDeleteFailure failure, string newExt)
    {
        if (failure?.Exception == null)
        {
            return;
        }
        ShowOperationDialog(
            string.Format(
                Resources.Error_BmsFileMoveFailed,
                failure.Path,
                newExt,
                DisplayedExceptionMessage.Format(failure.Exception)),
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    internal void ShowPendingRenameFailure(PendingExtensionRenameFailureReport failure)
    {
        if (failure?.Outcome?.FailureException == null || string.IsNullOrWhiteSpace(failure.FilePath))
        {
            return;
        }
        string message = failure.Outcome.FailedDuringDelete
            ? string.Format(
                Resources.Error_BmsFileDeleteFailed,
                failure.FilePath,
                DisplayedExceptionMessage.Format(failure.Outcome.FailureException))
            : string.Format(
                Resources.Error_BmsFileMoveFailed,
                failure.FilePath,
                failure.Outcome.FinalPath,
                DisplayedExceptionMessage.Format(failure.Outcome.FailureException));
        ShowOperationDialog(
            message,
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    internal void LogInfo(string info)
    {
        NLogWrapper.FileLogger?.Info(info);
    }

    internal void LogInstallPerformance(string message)
    {
        logInstallPerformance(message);
    }

    internal void ShowDriveRootBmsSkipped()
    {
        ShowOperationDialog(
            Resources.Warn_DriveRootBmsSkipped,
            Resources.MessageBoxTitle_Confirm,
            MessageBoxButton.OK,
            MessageBoxImage.Exclamation,
            MessageBoxResult.OK);
    }

    internal void ShowRenameFailed(FolderAutoRenamePlan plan)
    {
        ShowOperationDialog(
            string.Format(Resources.Error_RenameFailed, plan.SourceDirectory, plan.FailureException.Message),
            Resources.MessageBoxTitle_Error,
            MessageBoxButton.OK,
            MessageBoxImage.Hand,
            MessageBoxResult.OK);
    }

    private void InvokePostLeaseNotificationsBestEffort(IEnumerable<Action> notifications)
    {
        foreach (Action notification in notifications ?? [])
        {
            if (notification == null)
            {
                continue;
            }
            try
            {
                notification();
            }
            catch (Exception exception)
            {
                NLogWrapper.FileLogger?.Warn(
                    exception,
                    "library_file_operation_post_lease_notification_failed");
            }
        }
    }
}
