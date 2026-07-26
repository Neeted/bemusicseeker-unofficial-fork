using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    private sealed class LibraryFileOperationMutationBoundary : ILibraryFileOperationMutationBoundary
    {
        private readonly BMSLibrary library;

        internal LibraryFileOperationMutationBoundary(BMSLibrary library)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
        }

        public IDisposable EnterMutationSequence() => library.lr2SynchronizationOwner.EnterLr2MutationSequence();

        public IDisposable TryBeginMutation(string operation, bool showMessage)
            => library.lr2SynchronizationOwner.TryBeginMutation(operation, showMessage);

        public bool TryBlockMutation(string operation, bool showMessage)
            => library.lr2SynchronizationOwner.TryBlockMutation(operation, showMessage);

        public IDisposable BeginCollectionMutationScope() => library.packageLifecycleOwner.BeginCollectionMutationScope();
    }

    /// <summary>
    /// Composes the application-facing capabilities consumed by the file
    /// operation workflow.  The workflow itself is facade-independent; this
    /// adapter is the sole composition boundary for legacy aggregate services.
    /// </summary>
    private sealed class LibraryFileOperationPort : ILibraryFileOperationPort
    {
        private readonly BMSLibrary library;

        internal LibraryFileOperationPort(BMSLibrary library)
        {
            this.library = library ?? throw new ArgumentNullException(nameof(library));
        }

        public IDisposable EnterFolderMoveWriteScope() => library.libraryFileOperationSynchronization.EnterFolderMoveWriteScope();

        public IDisposable EnterFolderMoveReadScope() => library.libraryFileOperationSynchronization.EnterFolderMoveReadScope();

        public IDisposable EnterNormalInvalidExtensionRenameWriteScope() => library.libraryFileOperationSynchronization.EnterNormalInvalidExtensionRenameWriteScope();

        public IDisposable EnterPendingInvalidExtensionRenameWriteScope() => library.libraryFileOperationSynchronization.EnterPendingInvalidExtensionRenameWriteScope();

        public IDisposable EnterLibraryChartRemovalWriteScope() => library.libraryFileOperationSynchronization.EnterLibraryChartRemovalWriteScope();

        public IDisposable EnterFixInstallationDirectoryWriteScope() => library.libraryFileOperationSynchronization.EnterFixInstallationDirectoryWriteScope();

        public IDisposable EnterMergeWriteScope(long operationId) => library.libraryFileOperationSynchronization.EnterMergeWriteScope(operationId);

        public bool TryBlockMutation(string operation, bool showMessage)
            => library.libraryFileOperationSynchronization.TryBlockMutation(operation, showMessage);

        public List<FolderAutoRenamePlan> BuildRootFolderMovePlans(
            IEnumerable<ChartFile> selectedCharts,
            string destinationRootDirectory)
            => library.libraryFileOperationsService.BuildRootFolderMovePlans(
                ToLibraryChartRefs(selectedCharts),
                destinationRootDirectory);

        public LibraryMutationDelta BuildFolderMoveDelta(
            string sourceDirectory,
            string destinationDirectory,
            IEnumerable<ChartFile> sourceCharts,
            InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
            bool unregister,
            bool notifyStorageRowPathChanges)
            => library.libraryFileOperationsService.BuildFolderMoveDelta(
                sourceDirectory,
                destinationDirectory,
                ToLibraryChartRefs(sourceCharts),
                installDestinationOverlayCharts,
                library.ChartPackagesPending,
                library.ChartPackagesInstalled,
                unregister,
                notifyStorageRowPathChanges);

        public List<FolderAutoRenamePlan> BuildAutoRenamePlans(
            IEnumerable<ChartFile> selectedCharts,
            IEnumerable<string> rootFolders,
            bool renameRootFolder)
            => library.libraryFileOperationsService.BuildAutoRenamePlans(
                selectedCharts,
                rootFolders,
                renameRootFolder,
                library.CreateDirectLibraryChartSnapshotsInFolders,
                library.CreateChartFolderPathFromCharts,
                library.NormalizeAutoRenameFolderName);

        public List<FolderAutoRenamePlan> BuildAutoRenamePlansForSourceFolders(
            string parentDirectory)
        {
            List<string> sourceFolders = library.CreateOwnedRealPathChartDirectoriesUnsafe(parentDirectory);
            return library.libraryFileOperationsService.BuildAutoRenamePlansForSourceFolders(
                sourceFolders,
                library.getBMSDirectories(),
                renameRootFolder: false,
                library.CreateDirectLibraryChartSnapshotsInFolders,
                library.CreateChartFolderPathFromCharts,
                library.NormalizeAutoRenameFolderName);
        }

        public void MoveFolder(string sourceDirectory, string destinationDirectory)
            => library.libraryFileOperationsService.MoveFolder(
                sourceDirectory,
                destinationDirectory,
                library.fileMutationService,
                BMSLibrary.recursiveDirectoryTreeFileMutationOptions);

        public LibraryMutationDelta RenameLibraryFileExtensions(
            IEnumerable<ChartFile> charts,
            string newExtension,
            bool unregister)
            => library.libraryFileOperationsService.RenameLibraryFileExtensions(
                charts,
                newExtension,
                unregister,
                (file, requestedPath) => library.ProcessInvalidExtensionRename(file, requestedPath, unregister));

        public PendingExtensionRenameReport RenamePendingBmsFormatChartFileExtensions(
            IEnumerable<ChartFile> charts,
            string newExtension)
        {
            PendingExtensionRenameResult sourceResult = library.packageInstallService.RenamePendingBmsFormatChartFileExtensions(
                charts,
                newExtension,
                (file, requestedPath) => library.ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false));
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

        public PendingFileDeletionResult DeletePendingCharts(
            IEnumerable<ChartFile> charts,
            bool sendToRecycleBin,
            bool deleteContainingPackageFoldersWhenNoBms)
            => library.packageInstallService.DeletePendingCharts(
                charts,
                library.ChartPackagesPending,
                sendToRecycleBin,
                deleteContainingPackageFoldersWhenNoBms,
                library.fileMutationService,
                BMSLibrary.targetOnlyFileMutationOptions,
                BMSLibrary.recursiveDirectoryTreeFileMutationOptions);

        public LibraryMutationDelta FixInstallationDirectory(
            IEnumerable<ChartFile> charts,
            IPrimaryHashLookup existingHashes,
            IEnumerable<string> approvedDuplicateRemovalChartPaths,
            out List<ChartFile> chartsToRemove,
            out List<ChartFile> maintenanceCharts)
        {
            LibraryFixInstallationResult sourceResult = library.libraryFileOperationsService.FixInstallationDirectory(
                charts,
                (package, destinationDirectory) => library.MoveChartPackageFiles(
                    package,
                    destinationDirectory,
                    showMessageBoxOnInstallFail: true,
                    deleteAllContents: false,
                    existingHashes),
                chart => IsApprovedDuplicateRemoval(chart, approvedDuplicateRemovalChartPaths));
            chartsToRemove = [.. (sourceResult.ChartsToRemove ?? [])
                .Select(chart => chart?.ToChartFile())
                .Where(chart => chart != null)];
            maintenanceCharts = [.. (sourceResult.MaintenanceCharts ?? [])
                .Where(chart => chart != null)];
            return sourceResult.MutationDelta;
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
            return library.ShowOperationDialog(
                string.Format(Properties.Resources.Confirm_DuplicateReinstallSkipped, chart?.Path, string.Join(Environment.NewLine, library.GetDuplicateInstallRepairPaths(chart))),
                Properties.Resources.MessageBoxTitle_Confirm,
                UiDialogButton.YesNo,
                UiDialogIcon.Question,
                UiDialogDefaultResult.Yes) == UiDialogDefaultResult.Yes;
        }

        public LibraryMutationDelta PrepareMergeDirectory(
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
            LibraryMergeResult sourceResult = library.libraryFileOperationsService.PrepareMergeDirectory(
                sourceDirectory,
                destinationDirectory,
                ToLibraryChartRefs(sourceCharts),
                installDestinationOverlayCharts,
                library.ChartPackagesPending,
                library.ChartPackagesInstalled,
                excluded => library.CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, hashSnapshotReason, operationId));
            success = sourceResult.Success;
            preparedSourceCharts = [.. (sourceResult.SourceCharts ?? [])
                .Select(chart => chart?.ToChartFile())
                .Where(chart => chart != null)];
            existingHashes = sourceResult.ExistingHashes ?? EmptyPrimaryHashLookup.Instance;
            return sourceResult.ReferenceMutationDelta;
        }

        public List<string> getBMSDirectories() => library.getBMSDirectories();

        public string NormalizeAutoRenameFolderName(string folderName) => library.NormalizeAutoRenameFolderName(folderName);

        public List<ChartFile> CreateOwnedRealPathChartSnapshotsUnsafe(string directoryPath)
            => [.. library.CreateOwnedRealPathChartRefsUnsafe(directoryPath)
                .Select(chart => chart?.ToChartFile())
                .Where(chart => chart != null)];

        public List<string> CreateOwnedRealPathChartDirectoriesUnsafe(string directoryPath)
            => library.CreateOwnedRealPathChartDirectoriesUnsafe(directoryPath);

        public List<ChartFile> CreateOwnedStorageTargetChartSnapshotsForSubtreeDirectoryUnsafe(string directoryPath)
        {
            ChartStorageTargetSet targets = library.CreateOwnedStorageTargetsForSubtreeDirectoryUnsafe(directoryPath);
            return [.. targets.BmsFiles
                .Select(file => ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false))
                .Concat(targets.BmsonSongs.Select(song => ChartFileProjection.FromBmsonSong(song, includeWarningSnapshot: false)))
                .Where(chart => chart != null)];
        }

        public List<ChartFile> CreateDirectLibraryChartSnapshotsInFolders(IEnumerable<string> folderPaths)
            => library.CreateDirectLibraryChartSnapshotsInFolders([.. (folderPaths ?? [])]);

        public IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
            IEnumerable<ChartFile> excluded,
            string reason,
            long operationId)
            => library.CreateInstalledChartKeySnapshotExcludingChartsUnsafe(excluded, reason, operationId);

        public InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot()
            => library.installDestinationStateOwner.CreateOverlaySnapshot(out _);

        public void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPathsToRemove)
        {
            List<string> paths = [.. (chartPathsToRemove ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            if (paths.Count == 0)
            {
                return;
            }
            library.packageLifecycleOwner.ApplyPendingPackageMutationDelta(
                library.packageInstallService.BuildPendingPackageMutationDelta(
                    library.ChartPackagesPending,
                    chartPathsToRemove: paths));
        }

        public DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(
            string sourceDirectory,
            string destinationDirectory)
            => library.libraryFileOperationsService.MoveFolderAndUpdateReferences(
                sourceDirectory,
                destinationDirectory,
                library.directoryResourceLookupCache,
                library.fileMutationService,
                BMSLibrary.recursiveDirectoryTreeFileMutationOptions);

        public MovedFolderReferenceUpdateResult UpdateMovedFolderReferences(
            IEnumerable<LibraryFolderPathChange> movedFolders)
            => library.libraryFileOperationsService.UpdateMovedFolderReferences(
                movedFolders,
                library.directoryResourceLookupCache);

        public LibraryMutationDelta DeleteLibraryCharts(
            IEnumerable<ChartFile> charts,
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
            LibraryRemovalResult sourceResult = library.libraryFileOperationsService.DeleteLibraryCharts(
                ToLibraryChartRefs(charts),
                library.CreateOwnedCanonicalChartLookupUnsafe(),
                library.installDestinationStateOwner.CreateOverlaySnapshot(out _),
                library.ChartPackagesPending ?? [],
                library.directoryResourceLookupCache,
                sendToRecycleBin,
                folderPath => IsApprovedWholeFolderDelete(folderPath, approvedWholeFolderDeletePaths),
                library.fileMutationService,
                BMSLibrary.targetOnlyFileMutationOptions,
                BMSLibrary.recursiveDirectoryTreeFileMutationOptions);
            failures = [.. (sourceResult.Failures ?? []).Where(failure => failure != null)];
            inputChartCount = sourceResult.InputChartCount;
            canonicalChartCount = sourceResult.CanonicalChartCount;
            unresolvedChartCount = sourceResult.UnresolvedChartCount;
            pathOnlyInputCount = sourceResult.PathOnlyInputCount;
            removedChartCount = sourceResult.RemovedCharts?.Count ?? 0;
            folderDeleteCount = sourceResult.FolderDeleteCount;
            fileDeleteCount = sourceResult.FileDeleteCount;
            resourceIndexMutation = sourceResult.ResourceIndexMutation;
            return sourceResult.MutationDelta;
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
            return library.ShowOperationDialog(
                string.Format(Properties.Resources.Confirm_DeleteFolderWithNoBms, folderPath),
                Properties.Resources.MessageBoxTitle_Confirm,
                UiDialogButton.YesNo,
                UiDialogIcon.Question,
                UiDialogDefaultResult.Yes) == UiDialogDefaultResult.Yes;
        }

        public DirectoryResourceLookupCache.ReverseLookupMutationResult RemoveReverseLookupDirectoriesUnderSource(string sourceDirectory)
            => library.RemoveReverseLookupDirectoriesForFileOperation(sourceDirectory);

        public DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectories(ChartScanResult scan)
            => library.AddReverseLookupDirectoriesForFileOperation(scan);

        public string CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDirectory)
            => library.CreateChartFolderPathFromCharts(chartFiles, parentDirectory);

        public void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
            => library.ApplyLibraryMutationDelta(delta);

        public void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string performanceLogContext)
            => library.ApplyLibraryMutationDeltaWithPerformanceContext(delta, performanceLogContext);

        public void ApplyCatalogMaintenance(
            IEnumerable<ChartFile> charts,
            bool forceUpdate,
            ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
            string resourceHealthMutationReason)
        {
            _ = library.ApplyCatalogMaintenance(
                charts,
                forceUpdate,
                resourceHealthIndexUpdateMode: resourceHealthIndexUpdateMode,
                resourceHealthMutationReason: resourceHealthMutationReason);
        }

        public void InvalidateDuplicateChartGroupsCache() => library.InvalidateDuplicateChartGroupsCache();

        public void InvalidateInstalledDirectoryIndex() => library.InvalidateInstalledDirectoryIndex();

        public void LogReverseLookupMutationAndQueueWarmupIfNeeded(
            string reason,
            DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
            => library.LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);

        public void LogInstallPerformance(string message) => BMSLibrary.LogInstallPerformance(message);

        public void LogInstallPerformanceWarning(string message) => BMSLibrary.LogInstallPerformanceWarn(message);

        public UiDialogDefaultResult ShowOperationDialog(
            string message,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult)
            => library.ShowOperationDialog(message, caption, button, icon, defaultResult);

        public List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
            => BMSLibrary.NormalizeResourceMaintenanceTargetCharts(charts);

        public IEnumerable<string> GetDuplicateInstallRepairPaths(ChartFile chart)
            => library.GetDuplicateInstallRepairPaths(chart);

        public bool MoveMergePackageFiles(
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
            return library.MoveChartPackageFiles(
                package,
                destinationDirectory,
                showMessageBoxOnInstallFail: false,
                deleteAllContents: true,
                existingHashes);
        }

        private static List<LibraryChartRef> ToLibraryChartRefs(IEnumerable<ChartFile> charts)
            => [.. (charts ?? [])
                .Select(LibraryChartRef.FromChartFile)
                .Where(chart => chart != null)];

    }

    private static string GetDisplayedExceptionMessage(Exception exception)
        => DisplayedExceptionMessage.Format(exception);

    internal DirectoryResourceLookupCache.ReverseLookupMutationResult RemoveReverseLookupDirectoriesForFileOperation(string sourceDirectory)
    {
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        List<string> removedDirectories = [.. (directoryResourceLookupCache?.Keys ?? [])
            .Where(path => (path + Path.DirectorySeparatorChar).StartsWith(sourceDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))];
        foreach (string directory in removedDirectories)
        {
            mutation = mutation.Combine(directoryResourceLookupCache.RemoveDirWithResult(directory));
        }
        return mutation;
    }

    internal DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectoriesForFileOperation(ChartScanResult scan)
    {
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutation = DirectoryResourceLookupCache.ReverseLookupMutationResult.Empty;
        foreach (string chartDirectory in scan?.ChartDirectories ?? [])
        {
            mutation = mutation.Combine(directoryResourceLookupCache.AddDir(chartDirectory, scan));
        }
        return mutation;
    }
}
