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
    internal sealed class LibraryFileOperationOwner
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

        internal void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
        {
            owner.RemovePendingChartsFromPendingPackagesAndInstallRows(chartPaths);
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
