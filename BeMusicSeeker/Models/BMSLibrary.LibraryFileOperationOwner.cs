using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
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

    private readonly DirectoryResourceLookupCache directoryResourceLookupCache;

    private readonly IFileMutationService fileMutationService;

    private readonly InstallDestinationStateOwner installDestinationStateOwner;

    private readonly ScopedOperationDialogCoordinator dialogService;

    private readonly BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner;

    private readonly CatalogOwnedCollectionOwner catalogOwnedCollectionOwner;

    private readonly CatalogStorageRowsOwner catalogStorageRowsOwner;

    private readonly Func<IEnumerable<ChartFile>, string, long, IPrimaryHashLookup> createInstalledChartKeySnapshotExcludingChartsUnsafe;

    private readonly Func<IEnumerable<ChartFile>, string, string> createChartFolderPathFromCharts;

    private readonly Func<ChartFile, IEnumerable<string>> getDuplicateInstallRepairPaths;

    private readonly Action<LibraryMutationDelta, string> applyLibraryMutationDelta;

    private readonly Action<IEnumerable<ChartFile>, bool, ResourceHealthIndexUpdateMode, string> applyCatalogMaintenance;

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

    internal LibraryFileOperationOwner(
        LibraryFileOperationSynchronization synchronization,
        BmsLibraryLibraryFileOperationsService libraryFileOperationsService,
        BmsLibraryPackageInstallService packageInstallService,
        PackageLifecycleOwner packageLifecycleOwner,
        DirectoryResourceLookupCache directoryResourceLookupCache,
        IFileMutationService fileMutationService,
        InstallDestinationStateOwner installDestinationStateOwner,
        ScopedOperationDialogCoordinator dialogService,
        BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner,
        CatalogOwnedCollectionOwner catalogOwnedCollectionOwner,
        CatalogStorageRowsOwner catalogStorageRowsOwner,
        Func<IEnumerable<ChartFile>, string, long, IPrimaryHashLookup> createInstalledChartKeySnapshotExcludingChartsUnsafe,
        Func<IEnumerable<ChartFile>, string, string> createChartFolderPathFromCharts,
        Func<ChartFile, IEnumerable<string>> getDuplicateInstallRepairPaths,
        Action<LibraryMutationDelta, string> applyLibraryMutationDelta,
        Action<IEnumerable<ChartFile>, bool, ResourceHealthIndexUpdateMode, string> applyCatalogMaintenance,
        Action invalidateDuplicateChartGroupsCache,
        Action invalidateInstalledDirectoryIndex,
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
        this.directoryResourceLookupCache = directoryResourceLookupCache ?? throw new ArgumentNullException(nameof(directoryResourceLookupCache));
        this.fileMutationService = fileMutationService ?? throw new ArgumentNullException(nameof(fileMutationService));
        this.installDestinationStateOwner = installDestinationStateOwner ?? throw new ArgumentNullException(nameof(installDestinationStateOwner));
        this.dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        this.lr2SynchronizationOwner = lr2SynchronizationOwner ?? throw new ArgumentNullException(nameof(lr2SynchronizationOwner));
        this.catalogOwnedCollectionOwner = catalogOwnedCollectionOwner ?? throw new ArgumentNullException(nameof(catalogOwnedCollectionOwner));
        this.catalogStorageRowsOwner = catalogStorageRowsOwner ?? throw new ArgumentNullException(nameof(catalogStorageRowsOwner));
        this.createInstalledChartKeySnapshotExcludingChartsUnsafe = createInstalledChartKeySnapshotExcludingChartsUnsafe ?? throw new ArgumentNullException(nameof(createInstalledChartKeySnapshotExcludingChartsUnsafe));
        this.createChartFolderPathFromCharts = createChartFolderPathFromCharts ?? throw new ArgumentNullException(nameof(createChartFolderPathFromCharts));
        this.getDuplicateInstallRepairPaths = getDuplicateInstallRepairPaths ?? throw new ArgumentNullException(nameof(getDuplicateInstallRepairPaths));
        this.applyLibraryMutationDelta = applyLibraryMutationDelta ?? throw new ArgumentNullException(nameof(applyLibraryMutationDelta));
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

    private IDisposable EnterFolderMoveWriteScope()
    {
        return synchronization.EnterFolderMoveWriteScope();
    }

    private IDisposable EnterFolderMoveReadScope()
    {
        return synchronization.EnterFolderMoveReadScope();
    }

    private IDisposable EnterNormalInvalidExtensionRenameWriteScope()
    {
        return synchronization.EnterNormalInvalidExtensionRenameWriteScope();
    }

    private IDisposable EnterPendingInvalidExtensionRenameWriteScope()
    {
        return synchronization.EnterPendingInvalidExtensionRenameWriteScope();
    }

    private IDisposable EnterLibraryChartRemovalWriteScope()
    {
        return synchronization.EnterLibraryChartRemovalWriteScope();
    }

    private IDisposable EnterFixInstallationDirectoryWriteScope()
    {
        return synchronization.EnterFixInstallationDirectoryWriteScope();
    }

    private IDisposable EnterMergeWriteScope(long operationId)
    {
        return synchronization.EnterMergeWriteScope(operationId);
    }

    private bool TryBlockMutation(string operation, bool showMessage)
    {
        return synchronization.TryBlockMutation(operation, showMessage);
    }

    private void InvalidateInstalledDirectoryIndex()
    {
        invalidateInstalledDirectoryIndex();
    }

    internal void RunWithFolderMoveWriteLocks(Action action)
    {
        using IDisposable mutationScope = EnterFolderMoveWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        action();
    }

    internal void RunWithFolderMoveReadLocks(Action action)
    {
        using IDisposable mutationScope = EnterFolderMoveReadScope();
        action();
    }

    internal void RunWithNormalInvalidExtensionRenameWriteLocks(Action action)
    {
        using IDisposable mutationScope = EnterNormalInvalidExtensionRenameWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        action();
    }

    internal void RunWithPendingInvalidExtensionRenameWriteLocks(Action action)
    {
        using IDisposable mutationScope = EnterPendingInvalidExtensionRenameWriteScope();
        action();
    }

    private void RunWithLibraryChartRemovalWriteLocks(Action action)
    {
        using IDisposable mutationScope = EnterLibraryChartRemovalWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        action();
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
        return libraryFileOperationsService.MoveFolderAndUpdateReferences(
            sourceDirectory,
            destinationDirectory,
            directoryResourceLookupCache,
            fileMutationService,
            recursiveDirectoryTreeFileMutationOptions);
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

    private LibraryMutationDelta DeleteLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths,
        out List<LibraryDeleteFailure> failures,
        out int inputChartCount,
        out int canonicalChartCount,
        out int unresolvedChartCount,
        out int pathOnlyInputCount,
        out int removedChartCount,
        out int folderDeleteCount,
        out int fileDeleteCount,
        out DirectoryResourceLookupCache.ReverseLookupMutationResult resourceIndexMutation)
    {
        LibraryRemovalResult result = libraryFileOperationsService.DeleteLibraryCharts(
            charts,
            CreateOwnedCanonicalChartLookupUnsafe(),
            CreateInstallDestinationOverlayChartRefSnapshot(),
            packageLifecycleOwner.PendingPackages,
            directoryResourceLookupCache,
            sendToRecycleBin,
            path => IsApprovedWholeFolderDelete(path, approvedWholeFolderDeletePaths),
            fileMutationService,
            targetOnlyFileMutationOptions,
            recursiveDirectoryTreeFileMutationOptions);
        failures = result.Failures;
        inputChartCount = result.InputChartCount;
        canonicalChartCount = result.CanonicalChartCount;
        unresolvedChartCount = result.UnresolvedChartCount;
        pathOnlyInputCount = result.PathOnlyInputCount;
        removedChartCount = result.RemovedCharts?.Count ?? 0;
        folderDeleteCount = result.FolderDeleteCount;
        fileDeleteCount = result.FileDeleteCount;
        resourceIndexMutation = result.ResourceIndexMutation;
        return result.MutationDelta;
    }

    private void RemoveLibraryChartsCore(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        HashSet<string> approvedWholeFolderDeletes = approvedWholeFolderDeletePaths == null
            ? null
            : new HashSet<string>(approvedWholeFolderDeletePaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
        LibraryMutationDelta mutationDelta = DeleteLibraryCharts(
            charts,
            sendToRecycleBin,
            approvedWholeFolderDeletes,
            out List<LibraryDeleteFailure> failures,
            out int inputChartCount,
            out int canonicalChartCount,
            out int unresolvedChartCount,
            out int pathOnlyInputCount,
            out int removedChartCount,
            out int folderDeleteCount,
            out int fileDeleteCount,
            out DirectoryResourceLookupCache.ReverseLookupMutationResult resourceIndexMutation);
        LogInstallPerformance("delete_library_result input=" + inputChartCount
            + " canonical=" + canonicalChartCount
            + " unresolved=" + unresolvedChartCount
            + " pathOnly=" + pathOnlyInputCount
            + " removed=" + removedChartCount
            + " failures=" + failures.Count
            + " folderDeletes=" + folderDeleteCount
            + " fileDeletes=" + fileDeleteCount);
        LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", resourceIndexMutation);
        ApplyLibraryMutationDelta(mutationDelta);
        foreach (LibraryDeleteFailure failure in failures)
        {
            ShowDeleteFailure(failure);
        }
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

    internal void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
    {
        applyLibraryMutationDelta(delta, null);
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

    internal bool ApplyAutoRenamePlans(
        IEnumerable<FolderAutoRenamePlan> plans,
        Action<int, int, string> progressReporter)
    {
        return autoRenameBatchCoordinator.Apply(plans, progressReporter);
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

    private LibraryMutationDelta FixInstallationDirectory(
        IEnumerable<ChartFile> charts,
        IPrimaryHashLookup existingHashes,
        IEnumerable<string> approvedDuplicateRemovalChartPaths,
        out List<ChartFile> chartsToRemove,
        out List<ChartFile> maintenanceCharts)
    {
        LibraryFixInstallationResult sourceResult = libraryFileOperationsService.FixInstallationDirectory(
            charts,
            (package, destinationDirectory) => MoveChartPackageFiles(
                package,
                destinationDirectory,
                true,
                false,
                existingHashes),
            chart => IsApprovedDuplicateRemoval(chart, approvedDuplicateRemovalChartPaths));
        chartsToRemove = [.. (sourceResult.ChartsToRemove ?? [])
            .Select(chart => chart?.ToChartFile())
            .Where(chart => chart != null)];
        maintenanceCharts = [.. (sourceResult.MaintenanceCharts ?? [])
            .Where(chart => chart != null)];
        return sourceResult.MutationDelta;
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

    private void MoveFolder(string sourceDirectory, string destinationDirectory)
    {
        libraryFileOperationsService.MoveFolder(
            sourceDirectory,
            destinationDirectory,
            fileMutationService,
            recursiveDirectoryTreeFileMutationOptions);
    }

    private bool MoveMergePackageFiles(
        IReadOnlyList<ChartFile> chartSnapshots,
        string sourceDirectory,
        string destinationDirectory,
        IPrimaryHashLookup existingHashes)
    {
        List<PackageChartEntry> entries = [.. (chartSnapshots ?? [])
            .Select(PackageChartEntry.FromChart)
            .Where(entry => entry != null)];
        ChartPackage package = ChartPackage.FromChartEntries(entries);
        package.path = sourceDirectory;
        package.delete_parent = false;
        return MoveChartPackageFiles(
            package,
            destinationDirectory,
            false,
            true,
            existingHashes);
    }

    private void ApplyCatalogMaintenance(
        IEnumerable<ChartFile> charts,
        bool forceUpdate,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.DeltaOnUpdates,
        string resourceHealthMutationReason = null)
    {
        applyCatalogMaintenance(charts, forceUpdate, resourceHealthIndexUpdateMode, resourceHealthMutationReason);
    }

    private bool MoveChartPackageFiles(
        ChartPackage package,
        string destinationDirectory,
        bool showMessageBoxOnInstallFail,
        bool deleteAllContents,
        IPrimaryHashLookup existingHashes)
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
            existingHashes);
    }

    private RenameInvalidExtensionOutcome ProcessInvalidExtensionRename(
        BMSFile sourceFile,
        string requestedPath)
    {
        return libraryFileOperationsService.ProcessInvalidExtensionRename(
            sourceFile,
            requestedPath,
            fileMutationService,
            targetOnlyFileMutationOptions,
            logFileInfo,
            logFileWarning);
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
        return directoryResourceLookupCache.RemoveUnderSourceDirectory(sourceDirectory);
    }

    private DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectories(ChartScanResult scan)
    {
        return directoryResourceLookupCache.AddScanDirectories(scan);
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

    internal bool TryMoveLibraryChartFolderFileOnly(string sourceDirectory, string destinationDirectory)
    {
        if (sourceDirectory.Equals(destinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (LongPathFileSystem.EntryExists(destinationDirectory))
        {
            ShowMoveDestinationAlreadyExists(sourceDirectory, destinationDirectory);
            return false;
        }
        try
        {
            MoveFolder(sourceDirectory, destinationDirectory);
            return true;
        }
        catch (Exception moveException)
        {
            ShowFolderMoveFailed(sourceDirectory, destinationDirectory, moveException);
            return false;
        }
    }

    internal MovedFolderReferenceUpdateResult UpdateMovedFolderReferences(
        List<LibraryFolderPathChange> movedFolders)
    {
        return libraryFileOperationsService.UpdateMovedFolderReferences(
            movedFolders,
            directoryResourceLookupCache);
    }

    internal void ApplyLibraryMutationDeltaWithPerformanceContext(
        LibraryMutationDelta delta,
        string reason)
    {
        applyLibraryMutationDelta(delta, reason);
    }

    internal LibraryMutationDelta RenameLibraryFileExtensions(
        IEnumerable<ChartFile> targetCharts,
        string newExt,
        bool unregister)
    {
        return libraryFileOperationsService.RenameLibraryFileExtensions(
            targetCharts,
            newExt,
            unregister,
            (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath));
    }

    internal PendingExtensionRenameReport RenamePendingBmsFormatChartFileExtensions(
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        PendingExtensionRenameResult sourceResult = packageInstallService.RenamePendingBmsFormatChartFileExtensions(
            charts,
            newExt,
            (file, requestedPath) => ProcessInvalidExtensionRename(file, requestedPath));
        var result = new PendingExtensionRenameReport
        {
            Total = sourceResult.Total,
            Renamed = sourceResult.Renamed,
            DuplicateDeleted = sourceResult.DuplicateDeleted,
            Skipped = sourceResult.Skipped,
            Failed = sourceResult.Failed,
            TotalMs = sourceResult.TotalMs
        };
        result.ChartPathsToRemove.AddRange(sourceResult.ChartPathsToRemove);
        result.Failures.AddRange((sourceResult.Failures ?? [])
            .Where(failure => failure != null)
            .Select(failure => new PendingExtensionRenameFailureReport
            {
                FilePath = failure.File?.path,
                Outcome = failure.Outcome
            }));
        return result;
    }

    internal void RemoveLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        if (TryBlockMutation(nameof(BMSLibrary.RemoveLibraryCharts), showMessage: true))
        {
            return;
        }
        RunWithLibraryChartRemovalWriteLocks(
            () => RemoveLibraryChartsCore(charts, sendToRecycleBin, approvedWholeFolderDeletePaths));
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
        using IDisposable mutationScope = EnterPendingInvalidExtensionRenameWriteScope();
        {
            PendingFileDeletionResult result = DeletePendingCharts(
                charts,
                sendToRecycleBin,
                deleteContainingPackageFoldersWhenNoBms);
            foreach (PendingFileDeletionFailure failure in result.Failures)
            {
                if (failure?.Exception == null)
                {
                    continue;
                }
                if (failure.IsDirectory)
                {
                    ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
                else
                {
                    ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
            }
            RemovePendingChartsFromPendingPackagesAndInstallRows(result.ChartPathsToRemove);
        }
    }

    internal void FixInstallationDirectoryCharts(
        IEnumerable<ChartFile> charts,
        IEnumerable<string> approvedDuplicateRemovalChartPaths)
    {
        if (charts == null)
        {
            throw new ArgumentNullException(nameof(charts));
        }
        if (TryBlockMutation(nameof(BMSLibrary.FixInstallationDirectoryCharts), showMessage: true))
        {
            return;
        }

        HashSet<string> approvedDuplicateRemovalPaths = approvedDuplicateRemovalChartPaths == null
            ? null
            : new HashSet<string>(approvedDuplicateRemovalChartPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

        using IDisposable mutationScope = EnterFixInstallationDirectoryWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
        IPrimaryHashLookup existingHashes = CreateInstalledChartKeySnapshotExcludingChartsUnsafe(chartList);
        LibraryMutationDelta mutationDelta = FixInstallationDirectory(
            chartList,
            existingHashes,
            approvedDuplicateRemovalPaths,
            out List<ChartFile> chartsToRemove,
            out List<ChartFile> maintenanceCharts);
        ApplyLibraryMutationDelta(mutationDelta);
        if (chartsToRemove.Count > 0)
        {
            RemoveLibraryChartsCore(chartsToRemove.Select(LibraryChartRef.FromChartFile), sendToRecycleBin: true, approvedWholeFolderDeletePaths: []);
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

    internal void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
    {
        List<string> paths = [.. (chartPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Count == 0)
        {
            return;
        }
        packageLifecycleOwner.ApplyPendingPackageMutationDelta(
            packageInstallService.BuildPendingPackageMutationDelta(
                packageLifecycleOwner.PendingPackages,
                chartPathsToRemove: paths));
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
}
