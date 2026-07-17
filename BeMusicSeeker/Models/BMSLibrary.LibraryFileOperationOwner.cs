using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public partial class BMSLibrary
{
    /// <summary>
    /// Owns the file-operation corridors that change chart paths or extensions.
    /// Catalog and package state are handed to their canonical owners after the
    /// filesystem mutation succeeds; this object only composes those owners with
    /// the facade's UI and cache residuals.
    /// </summary>
    internal sealed partial class LibraryFileOperationOwner
    {
        private readonly BMSLibrary owner;

        private readonly AutoRenameBatchCoordinator autoRenameBatchCoordinator;

        internal LibraryFileOperationOwner(BMSLibrary owner)
        {
            this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
            autoRenameBatchCoordinator = new(this);
        }

        internal bool TryBlockLr2SongDbSyncMutation(string operation)
        {
            return owner.TryBlockLr2SongDbSyncMutation(operation);
        }

        internal bool IsLibraryRootFolder(string folderPath)
        {
            return owner.getBMSDirectories().Contains(folderPath, StringComparer.OrdinalIgnoreCase);
        }

        internal string NormalizeAutoRenameFolderName(string folderName)
        {
            return owner.NormalizeAutoRenameFolderName(folderName);
        }

        internal bool DirectoryExists(string folderPath)
        {
            return LongPathFileSystem.DirectoryExists(folderPath);
        }

        internal bool EntryExists(string path)
        {
            return LongPathFileSystem.EntryExists(path);
        }

        internal void RunWithFolderMoveWriteLocks(Action action)
        {
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            using (owner.rwlockPendingInstallCharts.GetWriterGuard())
            using (owner.rwlockBMSFiles.GetWriterGuard())
            {
                action();
            }
        }

        internal void RunWithFolderMoveReadLocks(Action action)
        {
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            using (owner.rwlockBMSFiles.GetReaderGuard())
            {
                action();
            }
        }

        internal void RunWithNormalInvalidExtensionRenameWriteLocks(Action action)
        {
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            using (owner.rwlockBMSFiles.GetWriterGuard())
            {
                action();
            }
        }

        internal void RunWithPendingInvalidExtensionRenameWriteLocks(Action action)
        {
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            using (owner.rwlockPendingInstallCharts.GetWriterGuard())
            using (owner.rwlockSongDBInstall.GetWriterGuard())
            {
                action();
            }
        }

        private void RunWithLibraryChartRemovalWriteLocks(Action action)
        {
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            using (owner.rwlockPendingInstallCharts.GetWriterGuard())
            using (owner.rwlockBMSFiles.GetWriterGuard())
            {
                action();
            }
        }

        internal List<LibraryChartRef> CreateNonNullChartRefList(IEnumerable<LibraryChartRef> charts)
        {
            return [.. (charts ?? []).Where(chart => chart != null)];
        }

        internal List<FolderAutoRenamePlan> BuildRootFolderMovePlans(
            List<LibraryChartRef> charts,
            string destinationDirectory)
        {
            return owner.libraryFileOperationsService.BuildRootFolderMovePlans(charts, destinationDirectory);
        }

        internal DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(
            string sourceDirectory,
            string destinationDirectory)
        {
            return owner.libraryFileOperationsService.MoveFolderAndUpdateReferences(
                sourceDirectory,
                destinationDirectory,
                owner.directoryResourceLookupCache,
                owner.fileMutationService,
                recursiveDirectoryTreeFileMutationOptions);
        }

        internal LibraryMutationDelta BuildFolderMoveDelta(
            string sourceDirectory,
            string destinationDirectory,
            bool unregister,
            bool notifyStorageRowPathChanges)
        {
            return owner.libraryFileOperationsService.BuildFolderMoveDelta(
                sourceDirectory,
                destinationDirectory,
                owner.CreateOwnedRealPathChartRefsUnsafe(sourceDirectory),
                owner.installDestinationStateOwner.CreateOverlaySnapshot(out _),
                owner.ChartPackagesPending,
                owner.ChartPackagesInstalled,
                unregister,
                notifyStorageRowPathChanges);
        }

        private LibraryRemovalResult DeleteLibraryCharts(
            IEnumerable<LibraryChartRef> charts,
            bool sendToRecycleBin,
            Func<string, bool> confirmDeleteWholeFolder)
        {
            return owner.libraryFileOperationsService.DeleteLibraryCharts(
                charts,
                owner.CreateOwnedCanonicalChartLookupUnsafe(),
                owner.installDestinationStateOwner.CreateOverlaySnapshot(out _),
                owner.ChartPackagesPending,
                owner.directoryResourceLookupCache,
                sendToRecycleBin,
                confirmDeleteWholeFolder,
                owner.fileMutationService,
                BMSLibrary.targetOnlyFileMutationOptions,
                BMSLibrary.recursiveDirectoryTreeFileMutationOptions);
        }

        private void RemoveLibraryChartsCore(
            IEnumerable<LibraryChartRef> charts,
            bool sendToRecycleBin,
            IEnumerable<string> approvedWholeFolderDeletePaths)
        {
            HashSet<string> approvedWholeFolderDeletes = approvedWholeFolderDeletePaths == null
                ? null
                : new HashSet<string>(approvedWholeFolderDeletePaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
            LibraryRemovalResult result = DeleteLibraryCharts(
                charts,
                sendToRecycleBin,
                folderPath => approvedWholeFolderDeletes != null
                    ? approvedWholeFolderDeletes.Contains(folderPath)
                    : ConfirmDeleteWholeFolder(folderPath));
            BMSLibrary.LogInstallPerformance("delete_library_result input=" + result.InputChartCount
                + " canonical=" + result.CanonicalChartCount
                + " unresolved=" + result.UnresolvedChartCount
                + " pathOnly=" + result.PathOnlyInputCount
                + " removed=" + result.RemovedCharts.Count
                + " failures=" + result.Failures.Count
                + " folderDeletes=" + result.FolderDeleteCount
                + " fileDeletes=" + result.FileDeleteCount);
            owner.LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", result.ResourceIndexMutation);
            owner.ApplyLibraryMutationDelta(result.MutationDelta);
            foreach (LibraryDeleteFailure failure in result.Failures)
            {
                ShowDeleteFailure(failure);
            }
        }

        private bool ConfirmDeleteWholeFolder(string folderPath)
        {
            return owner.ShowOperationDialog(
                string.Format(Resources.Confirm_DeleteFolderWithNoBms, folderPath),
                Resources.MessageBoxTitle_Confirm,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) == MessageBoxResult.Yes;
        }

        private void ShowDeleteFailure(LibraryDeleteFailure failure)
        {
            if (failure.IsDirectory)
            {
                owner.ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, BMSLibrary.GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
            else
            {
                owner.ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, BMSLibrary.GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
            }
        }

        internal void ApplyLibraryMutationDelta(LibraryMutationDelta delta)
        {
            owner.ApplyLibraryMutationDelta(delta);
        }

        internal void InvalidateDuplicateChartGroupsCache()
        {
            owner.InvalidateDuplicateChartGroupsCache();
        }

        internal void LogReverseLookupMutationAndQueueWarmupIfNeeded(
            string reason,
            DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult)
        {
            owner.LogReverseLookupMutationAndQueueWarmupIfNeeded(reason, mutationResult);
        }

        internal void ShowCannotRenameRootFolder(string sourceDirectory)
        {
            owner.ShowOperationDialog(
                string.Format(Resources.Warn_CannotRenameRootFolder, sourceDirectory),
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }

        internal void ShowRenameFolderNotExists(string sourceDirectory)
        {
            owner.ShowOperationDialog(
                string.Format(Resources.Warn_RenameFolderNotExists, sourceDirectory),
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }

        internal void ShowMoveDestinationAlreadyExists(string sourceDirectory, string destinationDirectory)
        {
            owner.ShowOperationDialog(
                string.Format(Resources.Warn_MoveDestAlreadyExists, sourceDirectory, destinationDirectory),
                Resources.MessageBoxTitle_Warning,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }

        internal void ShowMoveDestinationRootNotFound(string destinationDirectory)
        {
            owner.ShowOperationDialog(
                string.Format(Resources.Error_MoveDestRootNotFound, destinationDirectory),
                Resources.MessageBoxTitle_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK);
        }

        internal void ShowDriveRootCannotChangeRoot()
        {
            owner.ShowOperationDialog(
                Resources.Warn_DriveRootCannotChangeRoot,
                Resources.MessageBoxTitle_Confirm,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }

        internal void ShowFolderMoveFailed(string sourceDirectory, string destinationDirectory, Exception exception)
        {
            owner.ShowOperationDialog(
                string.Format(
                    Resources.Error_FolderMoveFailed,
                    sourceDirectory,
                    destinationDirectory,
                    BMSLibrary.GetDisplayedExceptionMessage(exception)),
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
            return owner.libraryFileOperationsService.BuildAutoRenamePlans(
                selectedCharts,
                rootFolders,
                renameRootFolder,
                owner.CreateDirectLibraryChartSnapshotsInFolders,
                owner.CreateChartFolderPathFromCharts,
                owner.NormalizeAutoRenameFolderName);
        }

        internal List<FolderAutoRenamePlan> BuildAutoRenamePlansForSourceFolders(
            string parentDirectory)
        {
            List<string> sourceFolders = owner.CreateOwnedRealPathChartDirectoriesUnsafe(parentDirectory);
            if (sourceFolders.Count == 0)
            {
                return [];
            }
            return owner.libraryFileOperationsService.BuildAutoRenamePlansForSourceFolders(
                sourceFolders,
                owner.getBMSDirectories(),
                renameRootFolder: false,
                owner.CreateDirectLibraryChartSnapshotsInFolders,
                owner.CreateChartFolderPathFromCharts,
                owner.NormalizeAutoRenameFolderName);
        }

        internal bool ApplyAutoRenamePlans(
            IEnumerable<FolderAutoRenamePlan> plans,
            Action<int, int, string> progressReporter)
        {
            return autoRenameBatchCoordinator.Apply(plans, progressReporter);
        }

        internal InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot()
        {
            return owner.installDestinationStateOwner.CreateOverlaySnapshot(out _);
        }

        internal LibraryMutationDelta BuildFolderMoveDelta(
            string sourceDirectory,
            string destinationDirectory,
            InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts)
        {
            return owner.libraryFileOperationsService.BuildFolderMoveDelta(
                sourceDirectory,
                destinationDirectory,
                owner.CreateOwnedRealPathChartRefsUnsafe(sourceDirectory),
                installDestinationOverlayCharts,
                owner.ChartPackagesPending,
                owner.ChartPackagesInstalled,
                unregister: false,
                notifyStorageRowPathChanges: false);
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
                owner.libraryFileOperationsService.MoveFolder(
                    sourceDirectory,
                    destinationDirectory,
                    owner.fileMutationService,
                    recursiveDirectoryTreeFileMutationOptions);
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
            return owner.libraryFileOperationsService.UpdateMovedFolderReferences(
                movedFolders,
                owner.directoryResourceLookupCache);
        }

        internal void ApplyLibraryMutationDeltaWithPerformanceContext(
            LibraryMutationDelta delta,
            string reason)
        {
            owner.ApplyLibraryMutationDeltaWithPerformanceContext(delta, reason);
        }

        internal LibraryMutationDelta RenameLibraryFileExtensions(
            IEnumerable<ChartFile> targetCharts,
            string newExt,
            bool unregister)
        {
            return owner.libraryFileOperationsService.RenameLibraryFileExtensions(
                targetCharts,
                newExt,
                unregister,
                (file, requestedPath) => owner.ProcessInvalidExtensionRename(file, requestedPath, unregister));
        }

        internal PendingExtensionRenameResult RenamePendingBmsFormatChartFileExtensions(
            IEnumerable<ChartFile> charts,
            string newExt)
        {
            return owner.packageInstallService.RenamePendingBmsFormatChartFileExtensions(
                charts,
                newExt,
                (file, requestedPath) => owner.ProcessInvalidExtensionRename(file, requestedPath, removeFromLibraryOnSuccess: false));
        }

        internal void RemoveLibraryCharts(
            IEnumerable<LibraryChartRef> charts,
            bool sendToRecycleBin,
            IEnumerable<string> approvedWholeFolderDeletePaths)
        {
            if (TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.RemoveLibraryCharts)))
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
            using (owner.rwlockBMSFilesInitializedMin.GetReaderGuard())
            using (owner.rwlockPendingInstallCharts.GetWriterGuard())
            using (owner.rwlockSongDBInstall.GetWriterGuard())
            {
                PendingFileDeletionResult result = owner.packageInstallService.DeletePendingCharts(
                    charts,
                    owner.ChartPackagesPending,
                    sendToRecycleBin,
                    deleteContainingPackageFoldersWhenNoBms,
                    owner.fileMutationService,
                    BMSLibrary.targetOnlyFileMutationOptions,
                    BMSLibrary.recursiveDirectoryTreeFileMutationOptions);
                foreach (PendingFileDeletionFailure failure in result.Failures)
                {
                    if (failure?.Exception == null)
                    {
                        continue;
                    }
                    if (failure.IsDirectory)
                    {
                        owner.ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, BMSLibrary.GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                    }
                    else
                    {
                        owner.ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, BMSLibrary.GetDisplayedExceptionMessage(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
            if (TryBlockLr2SongDbSyncMutation(nameof(BMSLibrary.FixInstallationDirectoryCharts)))
            {
                return;
            }

            HashSet<string> approvedDuplicateRemovalPaths = approvedDuplicateRemovalChartPaths == null
                ? null
                : new HashSet<string>(approvedDuplicateRemovalChartPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

            using (owner.rwlockBMSFilesInitializedAll.GetReaderGuard())
            using (owner.rwlockPendingInstallCharts.GetWriterGuard())
            using (owner.rwlockBMSFiles.GetWriterGuard())
            {
                List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
                IPrimaryHashLookup existingHashes = owner.CreateInstalledChartKeySnapshotExcludingChartsUnsafe(chartList);
                LibraryFixInstallationResult result = owner.libraryFileOperationsService.FixInstallationDirectory(
                    chartList,
                    (package, destinationDirectory) => MoveChartPackageFiles(package, destinationDirectory, existingHashes),
                    chart => approvedDuplicateRemovalPaths != null
                        ? !string.IsNullOrWhiteSpace(chart?.Path) && approvedDuplicateRemovalPaths.Contains(chart.Path)
                        : ConfirmDuplicateReinstallSkipped(chart));
                owner.ApplyLibraryMutationDelta(result.MutationDelta);
                if (result.ChartsToRemove.Count > 0)
                {
                    RemoveLibraryChartsCore(result.ChartsToRemove, sendToRecycleBin: true, approvedWholeFolderDeletePaths: []);
                }
                List<ChartFile> maintenanceTargets = NormalizeResourceMaintenanceTargetCharts(result.MaintenanceCharts);
                if (maintenanceTargets.Count > 0)
                {
                    owner.ApplyCatalogMaintenance(
                        maintenanceTargets,
                        forceUpdate: true,
                        resourceHealthMutationReason: "fix_installation_directory");
                }
            }
        }

        private bool MoveChartPackageFiles(
            ChartPackage package,
            string destinationDirectory,
            IPrimaryHashLookup existingHashes)
        {
            return owner.MoveChartPackageFiles(
                package,
                destinationDirectory,
                showMessageBoxOnInstallFail: true,
                deleteAllContents: false,
                existingHashes: existingHashes);
        }

        private bool ConfirmDuplicateReinstallSkipped(ChartFile chart)
        {
            return owner.ShowOperationDialog(
                string.Format(Resources.Confirm_DuplicateReinstallSkipped, chart.Path, string.Join(Environment.NewLine, owner.GetDuplicateInstallRepairPaths(chart))),
                Resources.MessageBoxTitle_Confirm,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes) == MessageBoxResult.Yes;
        }

        private List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
        {
            return BMSLibrary.NormalizeResourceMaintenanceTargetCharts(charts);
        }

        internal void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
        {
            List<string> paths = [.. (chartPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)];
            if (paths.Count == 0)
            {
                return;
            }
            owner.packageLifecycleOwner.ApplyPendingPackageMutationDelta(
                owner.BuildPendingPackageMutationDelta(chartPathsToRemove: paths));
        }

        internal void ShowNormalRenameFailure(LibraryDeleteFailure failure, string newExt)
        {
            if (failure?.Exception == null)
            {
                return;
            }
            owner.ShowOperationDialog(
                string.Format(
                    Resources.Error_BmsFileMoveFailed,
                    failure.Path,
                    newExt,
                    BMSLibrary.GetDisplayedExceptionMessage(failure.Exception)),
                Resources.MessageBoxTitle_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK);
        }

        internal void ShowPendingRenameFailure(PendingExtensionRenameFailure failure)
        {
            if (failure?.Outcome?.FailureException == null || failure.File == null)
            {
                return;
            }
            string message = failure.Outcome.FailedDuringDelete
                ? string.Format(
                    Resources.Error_BmsFileDeleteFailed,
                    failure.File.path,
                    BMSLibrary.GetDisplayedExceptionMessage(failure.Outcome.FailureException))
                : string.Format(
                    Resources.Error_BmsFileMoveFailed,
                    failure.File.path,
                    failure.Outcome.FinalPath,
                    BMSLibrary.GetDisplayedExceptionMessage(failure.Outcome.FailureException));
            owner.ShowOperationDialog(
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
            BMSLibrary.LogInstallPerformance(message);
        }

        internal void ShowDriveRootBmsSkipped()
        {
            owner.ShowOperationDialog(
                Resources.Warn_DriveRootBmsSkipped,
                Resources.MessageBoxTitle_Confirm,
                MessageBoxButton.OK,
                MessageBoxImage.Exclamation,
                MessageBoxResult.OK);
        }

        internal void ShowRenameFailed(FolderAutoRenamePlan plan)
        {
            owner.ShowOperationDialog(
                string.Format(Resources.Error_RenameFailed, plan.SourceDirectory, plan.FailureException.Message),
                Resources.MessageBoxTitle_Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK);
        }
    }
}
