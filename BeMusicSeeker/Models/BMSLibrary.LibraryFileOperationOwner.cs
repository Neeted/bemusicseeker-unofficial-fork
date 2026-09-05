using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
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
    /// <summary>
    /// Owns the file-operation corridors that change chart paths or extensions.
    /// Catalog and package state are handed to their canonical owners after the
    /// filesystem mutation succeeds.  The owner receives the canonical
    /// synchronization, filesystem, catalog, package, maintenance, and
    /// presentation capabilities directly; it does not retain the aggregate
    /// facade or a forwarding operation port.
    /// </summary>
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

    private readonly Func<IEnumerable<ChartFile>, string, long, IPrimaryHashLookup> createInstalledChartKeySnapshotExcludingChartsUnsafe;

    private readonly Func<IEnumerable<ChartFile>, string, string> createChartFolderPathFromCharts;

    private readonly Func<ChartFile, IEnumerable<string>> getDuplicateInstallRepairPaths;

    private readonly Func<LibraryMutationDelta, string, bool, bool, LibraryFileMutationCapability, Action<Action>, FileDbMutationCommitResult> applyLibraryMutationDeltaWithCapability;

    private readonly Func<LibraryMutationDelta, string, bool, Action<Action>, FileDbMutationCommitResult> applyLibraryMutationDeltaWithoutLr2NormalFolderSync;

    private readonly Func<IEnumerable<ChartFile>, bool, ResourceHealthIndexUpdateMode, string, MaintenanceWorkflowResult> applyCatalogMaintenance;

    private readonly Action invalidateDuplicateChartGroupsCache;

    private readonly Action invalidateInstalledDirectoryIndex;

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

    private sealed class DetachedFixTargetLookup
    {
        private readonly Dictionary<BMSFile, DetachedFixTarget> bmsByOwner = [];

        private readonly Dictionary<LR2SongDBExtended.bmson_song, DetachedFixTarget> bmsonByOwner = [];

        private readonly Dictionary<string, DetachedFixTarget> byIdentity = new(StringComparer.OrdinalIgnoreCase);

        private DetachedFixTargetLookup(IEnumerable<DetachedFixTarget> mappings)
        {
            foreach (DetachedFixTarget mapping in mappings ?? [])
            {
                if (mapping?.DetachedChart == null)
                {
                    continue;
                }
                BMSFile bmsOwner = mapping.DetachedChart.GetBmsStorageOwner();
                if (bmsOwner != null)
                {
                    bmsByOwner.TryAdd(bmsOwner, mapping);
                }
                LR2SongDBExtended.bmson_song bmsonOwner = mapping.DetachedChart.GetBmsonStorageOwner();
                if (bmsonOwner != null)
                {
                    bmsonByOwner.TryAdd(bmsonOwner, mapping);
                }
                string identityKey = CreateDetachedFixIdentityKey(mapping.DetachedChart);
                if (!string.IsNullOrWhiteSpace(identityKey))
                {
                    byIdentity.TryAdd(identityKey, mapping);
                }
            }
        }

        internal static DetachedFixTargetLookup Create(IEnumerable<DetachedFixTarget> mappings)
        {
            return new DetachedFixTargetLookup(mappings);
        }

        internal bool TryGet(ChartFile chart, out DetachedFixTarget mapping)
        {
            mapping = null;
            if (chart == null)
            {
                return false;
            }
            BMSFile bmsOwner = chart.GetBmsStorageOwner();
            if (bmsOwner != null && bmsByOwner.TryGetValue(bmsOwner, out mapping))
            {
                return true;
            }
            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null && bmsonByOwner.TryGetValue(bmsonOwner, out mapping))
            {
                return true;
            }
            return byIdentity.TryGetValue(CreateDetachedFixIdentityKey(chart), out mapping);
        }

        internal bool TryGet(LibraryChartRef chart, out DetachedFixTarget mapping)
        {
            mapping = null;
            if (chart == null)
            {
                return false;
            }
            BMSFile bmsOwner = chart.GetBmsStorageOwner();
            if (bmsOwner != null && bmsByOwner.TryGetValue(bmsOwner, out mapping))
            {
                return true;
            }
            LR2SongDBExtended.bmson_song bmsonOwner = chart.GetBmsonStorageOwner();
            if (bmsonOwner != null && bmsonByOwner.TryGetValue(bmsonOwner, out mapping))
            {
                return true;
            }
            return byIdentity.TryGetValue(CreateDetachedFixIdentityKey(chart), out mapping);
        }

        private static string CreateDetachedFixIdentityKey(ChartFile chart)
        {
            return chart == null
                ? null
                : CreateDetachedFixIdentityKey(chart.Kind, chart.Path, chart.Md5, chart.Sha256);
        }

        private static string CreateDetachedFixIdentityKey(LibraryChartRef chart)
        {
            return chart == null
                ? null
                : CreateDetachedFixIdentityKey(
                    chart.Kind == LibraryChartKind.Bms ? ChartFileKind.Bms : ChartFileKind.Bmson,
                    chart.Path,
                    chart.Md5,
                    chart.Sha256);
        }

        private static string CreateDetachedFixIdentityKey(
            ChartFileKind kind,
            string path,
            string md5,
            string sha256)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return kind + "|" + path + "|" + (md5 ?? string.Empty) + "|" + (sha256 ?? string.Empty);
        }
    }

    /// <summary>
    /// Creates the file-operation owner with the canonical catalog mutation
    /// delegates.  Ordinary catalog apply stays capability-free; only callers
    /// that own the final LR2 bridge provide a live mutation capability.
    /// </summary>
    internal LibraryFileOperationOwner(
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
        Func<IEnumerable<ChartFile>, string, long, IPrimaryHashLookup> createInstalledChartKeySnapshotExcludingChartsUnsafe,
        Func<IEnumerable<ChartFile>, string, string> createChartFolderPathFromCharts,
        Func<ChartFile, IEnumerable<string>> getDuplicateInstallRepairPaths,
        Func<IEnumerable<ChartFile>, bool, ResourceHealthIndexUpdateMode, string, MaintenanceWorkflowResult> applyCatalogMaintenance,
        Action invalidateDuplicateChartGroupsCache,
        Action invalidateInstalledDirectoryIndex,
        Action<string, DirectoryResourceLookupCache.ReverseLookupMutationResult> logReverseLookupMutationAndQueueWarmupIfNeeded,
        Action<string> logInstallPerformance,
        Action<string> logInstallPerformanceWarning,
        Action<string> logFileInfo,
        Action<Exception, string> logFileWarning,
        FileMutationOptions targetOnlyFileMutationOptions,
        FileMutationOptions recursiveDirectoryTreeFileMutationOptions,
        Func<LibraryMutationDelta, string, bool, bool, LibraryFileMutationCapability, Action<Action>, FileDbMutationCommitResult> applyLibraryMutationDeltaWithCapability,
        Func<LibraryMutationDelta, string, bool, Action<Action>, FileDbMutationCommitResult> applyLibraryMutationDeltaWithoutLr2NormalFolderSync)
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
        this.createInstalledChartKeySnapshotExcludingChartsUnsafe = createInstalledChartKeySnapshotExcludingChartsUnsafe ?? throw new ArgumentNullException(nameof(createInstalledChartKeySnapshotExcludingChartsUnsafe));
        this.createChartFolderPathFromCharts = createChartFolderPathFromCharts ?? throw new ArgumentNullException(nameof(createChartFolderPathFromCharts));
        this.getDuplicateInstallRepairPaths = getDuplicateInstallRepairPaths ?? throw new ArgumentNullException(nameof(getDuplicateInstallRepairPaths));
        this.applyLibraryMutationDeltaWithCapability = applyLibraryMutationDeltaWithCapability
            ?? throw new ArgumentNullException(nameof(applyLibraryMutationDeltaWithCapability));
        this.applyLibraryMutationDeltaWithoutLr2NormalFolderSync = applyLibraryMutationDeltaWithoutLr2NormalFolderSync
            ?? throw new ArgumentNullException(nameof(applyLibraryMutationDeltaWithoutLr2NormalFolderSync));
        this.applyCatalogMaintenance = applyCatalogMaintenance ?? throw new ArgumentNullException(nameof(applyCatalogMaintenance));
        this.invalidateDuplicateChartGroupsCache = invalidateDuplicateChartGroupsCache ?? throw new ArgumentNullException(nameof(invalidateDuplicateChartGroupsCache));
        this.invalidateInstalledDirectoryIndex = invalidateInstalledDirectoryIndex ?? throw new ArgumentNullException(nameof(invalidateInstalledDirectoryIndex));
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

    private bool TryBlockMutation(string operation, bool showMessage)
    {
        return synchronization.TryBlockMutation(operation, showMessage);
    }

    private void InvalidateInstalledDirectoryIndex()
    {
        invalidateInstalledDirectoryIndex();
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
            throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
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
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
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
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
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
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
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

    internal DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(
        string sourceDirectory,
        string destinationDirectory)
    {
        libraryFileOperationsService.MoveFolder(
            sourceDirectory,
            destinationDirectory,
            fileMutationService,
            recursiveDirectoryTreeFileMutationOptions);
        return resourceIndexOwner.MoveFolderReferences(sourceDirectory, destinationDirectory).MutationResult;
    }

    internal FileDbMutationPlan BuildFolderMoveMutationPlan(
        string sourceDirectory,
        string destinationDirectory)
    {
        return libraryFileOperationsService.BuildFolderMoveMutationPlan(
            sourceDirectory,
            destinationDirectory);
    }

    internal FileDbMutationExecutor CreateFileDbMutationExecutor(FileDbMutationPlan plan)
    {
        return new FileDbMutationExecutor(
            plan,
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions);
    }

    internal FileDbMutationCommitResult ApplyLibraryMutationDeltaForFileMutation(
        LibraryMutationDelta delta,
        string reason,
        LibraryFileMutationCapability capability,
        Action<Action> postLeaseNotificationObserver,
        bool suppressNormalRefreshNotification = false,
        bool suppressLr2NormalFolderSync = false)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);
        return applyLibraryMutationDeltaWithCapability(
            delta,
            reason,
            suppressNormalRefreshNotification,
            suppressLr2NormalFolderSync,
            capability,
            postLeaseNotificationObserver);
    }

    /// <summary>
    /// Applies the ordinary catalog/database portion of a file mutation while
    /// leaving the final LR2 bridge to the outer lease owner.
    /// </summary>
    internal FileDbMutationCommitResult ApplyLibraryMutationDeltaForFileMutationWithoutLr2NormalFolderSync(
        LibraryMutationDelta delta,
        string reason,
        Action<Action> postLeaseNotificationObserver,
        bool suppressNormalRefreshNotification = false)
    {
        ArgumentNullException.ThrowIfNull(postLeaseNotificationObserver);
        return applyLibraryMutationDeltaWithoutLr2NormalFolderSync(
            delta,
            reason,
            suppressNormalRefreshNotification,
            postLeaseNotificationObserver);
    }

    internal DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderReferencesAfterCommit(
        string sourceDirectory,
        string destinationDirectory)
    {
        return resourceIndexOwner.MoveFolderReferences(sourceDirectory, destinationDirectory).MutationResult;
    }

    internal LibraryMutationDelta BuildFolderMoveDelta(
        string sourceDirectory,
        string destinationDirectory,
        bool unregister,
        bool notifyStorageRowPathChanges)
    {
        return libraryFileOperationsService.BuildFolderMoveDelta(
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

    private LibraryMutationDelta ExecuteLibraryChartRemovalAfterAdmission(
        IReadOnlyList<LibraryChartRef> requestedCharts,
        bool sendToRecycleBin,
        IReadOnlyList<string> approvedWholeFolderDeletePaths,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> preflightTargets,
        IReadOnlyList<string> preflightUnresolvedPaths,
        LibraryFileMutationCapability mutationCapability,
        out List<LibraryDeleteFailure> failures,
        out LibraryChartRemovalOutcome filesystemOutcome,
        out int inputChartCount,
        out int canonicalChartCount,
        out int unresolvedChartCount,
        out int pathOnlyInputCount,
        out int removedChartCount,
        out int folderDeleteCount,
        out int fileDeleteCount,
        out DirectoryResourceLookupCache.ReverseLookupMutationResult resourceIndexMutation)
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
                LibraryFileOperationTargetSnapshot currentTarget = LibraryFileOperationTargetSnapshot.FromChart(
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
        var delta = new LibraryMutationDelta
        {
            SkippedCount = staleTargetCount + unresolved.Count,
            RenamedCount = 0,
            DuplicateDeletedCount = 0,
            TotalMs = 0L
        };
        foreach (int targetIndex in execution.RemovedTargetIndexes.Distinct())
        {
            if (targetIndex < 0 || targetIndex >= validTargets.Count)
            {
                continue;
            }
            AddOwnerRemovalRequest(delta, validTargets[targetIndex]);
        }
        foreach (LibraryDeleteFailure failure in unresolved)
        {
            delta.Failures.Add(failure);
        }
        delta.Failures.AddRange(execution.Failures);
        HashSet<string> deletedFolders = new(execution.DeletedFolderPaths, StringComparer.OrdinalIgnoreCase);
        var clearedInstallTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Entry = binding.Entry,
                    Chart = binding.Entry.Chart,
                    ClearInstallDestinationState = true
                });
            }
            else if (binding.Chart != null)
            {
                delta.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
                {
                    Chart = binding.Chart,
                    ClearInstallDestinationState = true
                });
            }
        }
        delta.InvalidateInstalledDirectoryIndex = delta.ChartRemoveRequests.Count > 0
            || delta.UpdatedInstallDestinations.Count > 0;
        delta.InvalidateParentFolderCache = delta.ChartRemoveRequests.Count > 0;
        delta.ClearDuplicatedCache = delta.ChartRemoveRequests.Count > 0
            || delta.UpdatedInstallDestinations.Count > 0;
        resourceIndexMutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string deletedFolderPath in execution.DeletedFolderPaths)
        {
            resourceIndexMutation = resourceIndexMutation.Combine(
                resourceIndexOwner.RemoveUnderSourceDirectory(deletedFolderPath).MutationResult);
        }
        failures = delta.Failures;
        removedChartCount = execution.RemovedTargetIndexes.Distinct().Count();
        folderDeleteCount = execution.FolderDeleteCount;
        fileDeleteCount = execution.FileDeleteCount;
        return delta;
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
        LibraryMutationDelta mutationDelta = ExecuteLibraryChartRemovalAfterAdmission(
            requestedCharts,
            sendToRecycleBin,
            [.. (approvedWholeFolderDeletePaths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)],
            preflightTargets,
            preflightUnresolvedPaths,
            mutationCapability,
            out List<LibraryDeleteFailure> failures,
            out LibraryChartRemovalOutcome filesystemOutcome,
            out int inputChartCount,
            out int canonicalChartCount,
            out int unresolvedChartCount,
            out int pathOnlyInputCount,
            out int removedChartCount,
            out int folderDeleteCount,
            out int fileDeleteCount,
            out DirectoryResourceLookupCache.ReverseLookupMutationResult resourceIndexMutation);
        string resultLog = "delete_library_result input=" + inputChartCount
            + " canonical=" + canonicalChartCount
            + " unresolved=" + unresolvedChartCount
            + " pathOnly=" + pathOnlyInputCount
            + " removed=" + removedChartCount
            + " failures=" + failures.Count
            + " folderDeletes=" + folderDeleteCount
            + " fileDeletes=" + fileDeleteCount;
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        FileDbMutationCommitResult commit = ApplyLibraryMutationDeltaForFileMutation(
            mutationDelta, "delete_library", mutationCapability,
            action => postLeaseNotifications.Add(action));
        postLeaseNotifications.Add(() => LogInstallPerformance(resultLog));
        // Preserve the existing success-only warmup boundary even though catalog
        // failure now returns facts instead of throwing past these effects.
        if (commit.DurableCommit && commit.Failure == null)
            postLeaseNotifications.Add(() => LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", resourceIndexMutation));
        var outcome = new LibraryChartRemovalOutcome(filesystemOutcome.Targets, true,
            commit.DurableCommit, commit.Failure ?? (!commit.DurableCommit
                ? new InvalidOperationException("Catalog mutation did not produce a durable receipt.") : null));
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

    /// <summary>
    /// Applies a catalog delta under the caller's already-owned file mutation
    /// lease. Canonical state is completed before release; only public
    /// notifications are returned to the command owner.
    /// </summary>
    internal void ApplyLibraryMutationDeltaUnderExistingReservation(
        LibraryMutationDelta delta,
        string reason,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        FileDbMutationCommitResult result = ApplyLibraryMutationDeltaForFileMutation(
            delta,
            reason,
            suppressNormalRefreshNotification: false,
            capability: mutationCapability,
            postLeaseNotificationObserver: action => postLeaseNotifications.Add(action));
        if (result.Failure != null)
        {
            ExceptionDispatchInfo.Capture(result.Failure).Throw();
        }
        if (!result.DurableCommit)
        {
            throw result.Failure
                ?? new InvalidOperationException("Catalog mutation did not produce a durable receipt.");
        }
        // The catalog owner supplies the immutable notification action once
        // canonical state has been finalized under this same lease.
        // No notification action is stored on the receipt.
    }

    internal void InvalidateDuplicateChartGroupsCache()
    {
        invalidateDuplicateChartGroupsCache();
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
    /// Applies the planned folder renames and returns the durable batch receipt.
    /// LR2 synchronization remains owned by the final catalog mutation bridge.
    /// </summary>
    internal AutoRenameBatchResult ApplyAutoRenamePlansWithReceipt(
        IEnumerable<FolderAutoRenamePlan> plans,
        Action<int, int, string> progressReporter,
        ICollection<Action> postLeaseNotifications)
    {
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        return autoRenameBatchCoordinator.ApplyWithReceipts(
            plans,
            postLeaseNotifications,
            progressReporter);
    }

    internal InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot()
    {
        return installDestinationStateOwner.CreateOverlaySnapshot(out _);
    }

    internal LibraryMutationDelta BuildFolderMoveDelta(
        string sourceDirectory,
        string destinationDirectory,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts)
    {
        return libraryFileOperationsService.BuildFolderMoveDelta(
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
        return createInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, reason, operationId);
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

    private static LibraryChartRef CreateOwnerChartRef(LibraryFileOperationTargetSnapshot target)
    {
        if (target?.BmsOwner != null)
        {
            return LibraryChartRef.FromBmsFile(target.BmsOwner);
        }
        if (target?.BmsonOwner != null)
        {
            return LibraryChartRef.FromBmsonSong(target.BmsonOwner);
        }
        return null;
    }

    private static void CopyFixMutationDelta(
        LibraryMutationDelta destination,
        LibraryMutationDelta source,
        DetachedFixTargetLookup mappingLookup)
    {
        if (destination == null || source == null)
        {
            return;
        }
        foreach (LibraryChartPathChange sourcePathChange in source.ChartPathChanges ?? [])
        {
            DetachedFixTarget mapping = null;
            mappingLookup?.TryGet(sourcePathChange?.Chart, out mapping);
            ChartFile ownerChart = CreateOwnerChartSnapshot(mapping?.Original);
            if (mapping == null || ownerChart == null || string.IsNullOrWhiteSpace(sourcePathChange?.NewPath))
            {
                continue;
            }
            destination.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ownerChart,
                OldPath = mapping.Original.SourcePath,
                NewPath = sourcePathChange.NewPath
            });
        }
        foreach (LibraryInstallDestinationChange sourceInstallDestinationChange in source.UpdatedInstallDestinations ?? [])
        {
            DetachedFixTarget mapping = null;
            mappingLookup?.TryGet(sourceInstallDestinationChange?.Chart, out mapping);
            ChartFile ownerChart = CreateOwnerChartSnapshot(mapping?.Original);
            if (mapping == null || ownerChart == null)
            {
                continue;
            }
            destination.UpdatedInstallDestinations.Add(new LibraryInstallDestinationChange
            {
                Chart = ownerChart,
                NewInstallDestination = sourceInstallDestinationChange.NewInstallDestination,
                ClearInstallDestinationState = sourceInstallDestinationChange.ClearInstallDestinationState
            });
        }
        destination.NotifyStorageRowPathChanges |= source.NotifyStorageRowPathChanges;
        destination.RaiseInstalledPackagesChanged |= source.RaiseInstalledPackagesChanged;
        destination.InvalidateInstalledDirectoryIndex |= source.InvalidateInstalledDirectoryIndex;
        destination.InvalidateParentFolderCache |= source.InvalidateParentFolderCache;
        destination.ClearDuplicatedCache |= source.ClearDuplicatedCache;
        destination.TotalMs = source.TotalMs;
    }

    private static List<ChartFile> MapMaintenanceCharts(
        IEnumerable<ChartFile> detachedCharts,
        DetachedFixTargetLookup mappingLookup)
    {
        var result = new List<ChartFile>();
        var seenOwners = new HashSet<object>();
        foreach (ChartFile detachedChart in detachedCharts ?? [])
        {
            DetachedFixTarget mapping = null;
            mappingLookup?.TryGet(detachedChart, out mapping);
            ChartFile ownerChart = CreateOwnerChartSnapshot(mapping?.Original);
            if (ownerChart == null)
            {
                continue;
            }
            object owner = ownerChart.GetBmsStorageOwner() ?? (object)ownerChart.GetBmsonStorageOwner() ?? ownerChart;
            if (seenOwners.Add(owner))
            {
                result.Add(ownerChart);
            }
        }
        return result;
    }

    private LibraryMutationDelta FixInstallationDirectoryAfterAdmission(
        IReadOnlyList<ChartFile> requestedCharts,
        IReadOnlyList<LibraryFileOperationTargetSnapshot> preflightTargets,
        IReadOnlyList<string> approvedDuplicateRemovalChartPaths,
        LibraryFileMutationCapability mutationCapability,
        Action<Action> deferDiagnosticEffect,
        out List<ChartFile> chartsToRemove,
        out List<ChartFile> maintenanceCharts)
    {
        var mappings = new List<DetachedFixTarget>();
        var staleFailures = new List<LibraryDeleteFailure>();
        List<ChartFile> detachedCharts;
        IPrimaryHashLookup existingHashes;
        int staleTargetCount = 0;
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
                    staleTargetCount++;
                    staleFailures.Add(new LibraryDeleteFailure
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
                    staleTargetCount++;
                    staleFailures.Add(new LibraryDeleteFailure
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
                staleTargetCount++;
                staleFailures.Add(new LibraryDeleteFailure
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

        DetachedFixTargetLookup mappingLookup = DetachedFixTargetLookup.Create(mappings);
        LibraryFixInstallationResult sourceResult = libraryFileOperationsService.FixInstallationDirectory(
            detachedCharts,
            (package, destinationDirectory) => MoveChartPackageFiles(
                package,
                destinationDirectory,
                showMessageBoxOnInstallFail: true,
                deleteAllContents: false,
                existingHashes: existingHashes,
                deferDiagnosticEffect: deferDiagnosticEffect),
            chart => IsApprovedDuplicateRemoval(chart, approvedDuplicateRemovalChartPaths));
        var delta = new LibraryMutationDelta
        {
            SkippedCount = staleTargetCount + sourceResult.DuplicateSkippedCount,
            TotalMs = sourceResult.TotalMs
        };
        delta.Failures.AddRange(staleFailures);
        CopyFixMutationDelta(delta, sourceResult.MutationDelta, mappingLookup);
        chartsToRemove = [];
        foreach (LibraryChartRef chartToRemove in sourceResult.ChartsToRemove ?? [])
        {
            mappingLookup.TryGet(chartToRemove, out DetachedFixTarget mapping);
            ChartFile ownerChart = CreateOwnerChartSnapshot(mapping?.Original);
            if (ownerChart != null)
            {
                chartsToRemove.Add(ownerChart);
            }
        }
        chartsToRemove = [.. chartsToRemove
            .GroupBy(
                chart => chart.Kind + ":" + chart.Path,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())];
        maintenanceCharts = MapMaintenanceCharts(sourceResult.MaintenanceCharts, mappingLookup);
        return delta;
    }

    private LibraryMutationDelta PrepareMergeDirectory(
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
            excluded => CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, hashSnapshotReason, operationId));
        success = sourceResult.Success;
        preparedSourceCharts = [.. (sourceResult.SourceCharts ?? [])
            .Select(chart => chart?.ToChartFile())
            .Where(chart => chart != null)];
        existingHashes = sourceResult.ExistingHashes ?? EmptyPrimaryHashLookup.Instance;
        return sourceResult.ReferenceMutationDelta;
    }

    private MaintenanceWorkflowResult ApplyCatalogMaintenance(
        IEnumerable<ChartFile> charts,
        bool forceUpdate,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.DeltaOnUpdates,
        string resourceHealthMutationReason = null)
    {
        return applyCatalogMaintenance(charts, forceUpdate, resourceHealthIndexUpdateMode, resourceHealthMutationReason);
    }

    private bool MoveChartPackageFiles(
        ChartPackage package,
        string destinationDirectory,
        bool showMessageBoxOnInstallFail,
        bool deleteAllContents,
        IPrimaryHashLookup existingHashes,
        Action<Action> deferDiagnosticEffect)
    {
        return packageInstallService.MovePackageFiles(
            package,
            destinationDirectory,
            lr2SynchronizationOwner.CurrentOptionsSnapshot,
            createChartFolderPathFromCharts,
            DisplayedExceptionMessage.Format,
            fileMutationService,
            dialogService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions,
            LogInstallPerformance,
            showMessageBoxOnInstallFail,
            deleteAllContents,
            existingHashes,
            excludedComponentPaths: null,
            deferDiagnosticEffect: deferDiagnosticEffect);
    }

    private List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
    {
        return [.. (charts ?? []).Where(chart => chart != null)];
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
        LibraryMutationDelta delta,
        LibraryFileOperationTargetSnapshot target)
    {
        if (delta == null || target == null)
        {
            return;
        }
        OwnedChartRemoveRequest request = target.BmsOwner != null
            ? OwnedChartRemoveRequest.FromOwnerReference(target.BmsOwner)
            : OwnedChartRemoveRequest.FromOwnerReference(target.BmsonOwner);
        if (request != null)
        {
            delta.ChartRemoveRequests.Add(request);
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
            ChartPackage snapshot = ChartPackage.FromChartEntries(entries);
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
                ChartFile chart = chartRef?.ToChartFile();
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
            && string.Equals(binding.Path, target.Path, StringComparison.OrdinalIgnoreCase)
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
            && string.Equals(chart.Path, target.Path, StringComparison.OrdinalIgnoreCase)
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
    internal LibraryMutationDelta RenameLibraryFileExtensionsAfterAdmission(
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
        var delta = new LibraryMutationDelta
        {
            RenamedCount = execution.RenamedCount,
            DuplicateDeletedCount = execution.DuplicateDeletedCount,
            SkippedCount = execution.SkippedCount + staleTargetCount,
            TotalMs = execution.TotalMs
        };
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
                    if (unregister)
                    {
                        AddOwnerRemovalRequest(delta, target);
                    }
                    else
                    {
                        ChartFile ownerChart = CreateOwnerChartSnapshot(target);
                        if (ownerChart != null)
                        {
                            delta.ChartPathChanges.Add(new LibraryChartPathChange
                            {
                                Chart = ownerChart,
                                OldPath = target.SourcePath,
                                NewPath = outcome.FinalPath
                            });
                            delta.NotifyStorageRowPathChanges = true;
                        }
                    }
                    break;
                case RenameInvalidExtensionAction.DeletedAsDuplicate:
                    AddOwnerRemovalRequest(delta, target);
                    break;
                default:
                    if (outcome.FailureException != null)
                    {
                        delta.Failures.Add(new LibraryDeleteFailure
                        {
                            Path = target.SourcePath,
                            Exception = outcome.FailureException,
                            IsDirectory = false
                        });
                    }
                    break;
            }
        }
        delta.InvalidateInstalledDirectoryIndex = delta.ChartPathChanges.Count > 0 || delta.ChartRemoveRequests.Count > 0;
        delta.InvalidateParentFolderCache = delta.ChartPathChanges.Count > 0 || delta.ChartRemoveRequests.Count > 0;
        return delta;
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

    /// <summary>Returns observed filesystem and catalog facts after releasing the deletion lease.</summary>
    internal LibraryChartRemovalOutcome RemoveLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        if (TryBlockMutation(nameof(BMSLibrary.RemoveLibraryCharts), showMessage: true))
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

    /// <summary>Returns optional deletion facts; a deletion catalog failure stops dependent repair maintenance.</summary>
    internal LibraryChartRemovalOutcome FixInstallationDirectoryCharts(
        IEnumerable<ChartFile> charts,
        IEnumerable<string> approvedDuplicateRemovalChartPaths)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (TryBlockMutation(nameof(BMSLibrary.FixInstallationDirectoryCharts), showMessage: true))
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

        LibraryChartRemovalOutcome removalOutcome = null;
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
                LibraryMutationDelta mutationDelta = FixInstallationDirectoryAfterAdmission(
                    chartList,
                    preflightTargets,
                    approvedDuplicateRemovalPaths,
                    mutationCapability,
                    action => postLeaseNotifications.Add(action),
                    out List<ChartFile> chartsToRemove,
                    out List<ChartFile> maintenanceCharts);
                foreach (LibraryDeleteFailure failure in mutationDelta.Failures)
                {
                    postLeaseNotifications.Add(() => ShowDeleteFailure(failure));
                }
                ApplyLibraryMutationDeltaUnderExistingReservation(
                    mutationDelta,
                    "fix_installation_directory",
                    mutationCapability,
                    postLeaseNotifications);
                if (chartsToRemove.Count > 0)
                {
                    removalOutcome = RemoveLibraryChartsCore(
                        chartsToRemove.Select(LibraryChartRef.FromChartFile),
                        sendToRecycleBin: true,
                        approvedWholeFolderDeletePaths: [],
                        mutationCapability,
                        postLeaseNotifications);
                    if (removalOutcome.CatalogFailure != null)
                        throw new LibraryChartRemovalException(removalOutcome);
                }
                List<ChartFile> maintenanceTargets = NormalizeResourceMaintenanceTargetCharts(maintenanceCharts);
                if (maintenanceTargets.Count > 0)
                {
                    ApplyCatalogMaintenance(
                        maintenanceTargets,
                        forceUpdate: true,
                        resourceHealthMutationReason: "fix_installation_directory");
                }
            }
        }
        finally
        {
            InvokePostLeaseNotificationsBestEffort(postLeaseNotifications);
        }
        return removalOutcome;
    }

    /// <summary>
    /// Applies the pending-package/install-row removal under the active file
    /// mutation lease and queues collection publication for the caller's
    /// post-lease effect list.
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
