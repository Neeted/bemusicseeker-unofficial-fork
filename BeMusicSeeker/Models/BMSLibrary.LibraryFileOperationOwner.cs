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

namespace BeMusicSeeker.Models;

internal sealed partial class LibraryFileOperationOwner
{
    /// <summary>
    /// Owns the file-operation corridors that change chart paths or extensions.
    /// Catalog and package state are handed to their canonical owners after the
    /// filesystem mutation succeeds.  The owner consumes an explicit operation
    /// port and therefore does not retain the aggregate facade.
    /// </summary>
    private readonly ILibraryFileOperationPort owner;

    private readonly AutoRenameBatchCoordinator autoRenameBatchCoordinator;

    internal LibraryFileOperationOwner(ILibraryFileOperationPort owner)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        autoRenameBatchCoordinator = new(this);
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
        using IDisposable mutationScope = owner.EnterFolderMoveWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        action();
    }

    internal void RunWithFolderMoveReadLocks(Action action)
    {
        using IDisposable mutationScope = owner.EnterFolderMoveReadScope();
        action();
    }

    internal void RunWithNormalInvalidExtensionRenameWriteLocks(Action action)
    {
        using IDisposable mutationScope = owner.EnterNormalInvalidExtensionRenameWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        action();
    }

    internal void RunWithPendingInvalidExtensionRenameWriteLocks(Action action)
    {
        using IDisposable mutationScope = owner.EnterPendingInvalidExtensionRenameWriteScope();
        action();
    }

    private void RunWithLibraryChartRemovalWriteLocks(Action action)
    {
        using IDisposable mutationScope = owner.EnterLibraryChartRemovalWriteScope();
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
        return owner.BuildRootFolderMovePlans(charts, destinationDirectory);
    }

    internal DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(
        string sourceDirectory,
        string destinationDirectory)
    {
        return owner.MoveFolderAndUpdateReferences(sourceDirectory, destinationDirectory);
    }

    internal LibraryMutationDelta BuildFolderMoveDelta(
        string sourceDirectory,
        string destinationDirectory,
        bool unregister,
        bool notifyStorageRowPathChanges)
    {
        return owner.BuildFolderMoveDelta(
            sourceDirectory,
            destinationDirectory,
            owner.CreateOwnedRealPathChartSnapshotsUnsafe(sourceDirectory),
            owner.CreateInstallDestinationOverlayChartRefSnapshot(),
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
        return owner.DeleteLibraryCharts(
            (charts ?? []).Select(chart => chart?.ToChartFile()),
            sendToRecycleBin,
            approvedWholeFolderDeletePaths,
            out failures,
            out inputChartCount,
            out canonicalChartCount,
            out unresolvedChartCount,
            out pathOnlyInputCount,
            out removedChartCount,
            out folderDeleteCount,
            out fileDeleteCount,
            out resourceIndexMutation);
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
        owner.LogInstallPerformance("delete_library_result input=" + inputChartCount
            + " canonical=" + canonicalChartCount
            + " unresolved=" + unresolvedChartCount
            + " pathOnly=" + pathOnlyInputCount
            + " removed=" + removedChartCount
            + " failures=" + failures.Count
            + " folderDeletes=" + folderDeleteCount
            + " fileDeletes=" + fileDeleteCount);
        owner.LogReverseLookupMutationAndQueueWarmupIfNeeded("delete_library", resourceIndexMutation);
        owner.ApplyLibraryMutationDelta(mutationDelta);
        foreach (LibraryDeleteFailure failure in failures)
        {
            ShowDeleteFailure(failure);
        }
    }

    private void ShowDeleteFailure(LibraryDeleteFailure failure)
    {
        if (failure.IsDirectory)
        {
            owner.ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
        }
        else
        {
            owner.ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
        return owner.BuildAutoRenamePlans(
            selectedCharts,
            rootFolders,
            renameRootFolder);
    }

    internal List<FolderAutoRenamePlan> BuildAutoRenamePlansForSourceFolders(
        string parentDirectory)
    {
        return owner.BuildAutoRenamePlansForSourceFolders(parentDirectory);
    }

    internal bool ApplyAutoRenamePlans(
        IEnumerable<FolderAutoRenamePlan> plans,
        Action<int, int, string> progressReporter)
    {
        return autoRenameBatchCoordinator.Apply(plans, progressReporter);
    }

    internal InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot()
    {
        return owner.CreateInstallDestinationOverlayChartRefSnapshot();
    }

    internal LibraryMutationDelta BuildFolderMoveDelta(
        string sourceDirectory,
        string destinationDirectory,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts)
    {
        return owner.BuildFolderMoveDelta(
            sourceDirectory,
            destinationDirectory,
            owner.CreateOwnedRealPathChartSnapshotsUnsafe(sourceDirectory),
            installDestinationOverlayCharts,
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
            owner.MoveFolder(sourceDirectory, destinationDirectory);
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
        return owner.UpdateMovedFolderReferences(movedFolders);
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
        return owner.RenameLibraryFileExtensions(
            targetCharts,
            newExt,
            unregister);
    }

    internal PendingExtensionRenameReport RenamePendingBmsFormatChartFileExtensions(
        IEnumerable<ChartFile> charts,
        string newExt)
    {
        return owner.RenamePendingBmsFormatChartFileExtensions(
            charts,
            newExt);
    }

    internal void RemoveLibraryCharts(
        IEnumerable<LibraryChartRef> charts,
        bool sendToRecycleBin,
        IEnumerable<string> approvedWholeFolderDeletePaths)
    {
        if (owner.TryBlockMutation(nameof(BMSLibrary.RemoveLibraryCharts), showMessage: true))
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
        using IDisposable mutationScope = owner.EnterPendingInvalidExtensionRenameWriteScope();
        {
            PendingFileDeletionResult result = owner.DeletePendingCharts(
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
                    owner.ShowOperationDialog(string.Format(Resources.Error_FolderOrTrashDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                }
                else
                {
                    owner.ShowOperationDialog(string.Format(Resources.Error_BmsFileDeleteFailed, failure.Path, DisplayedExceptionMessage.Format(failure.Exception)), Resources.MessageBoxTitle_Error, MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
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
        if (owner.TryBlockMutation(nameof(BMSLibrary.FixInstallationDirectoryCharts), showMessage: true))
        {
            return;
        }

        HashSet<string> approvedDuplicateRemovalPaths = approvedDuplicateRemovalChartPaths == null
            ? null
            : new HashSet<string>(approvedDuplicateRemovalChartPaths.Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);

        using IDisposable mutationScope = owner.EnterFixInstallationDirectoryWriteScope();
        if (mutationScope == null)
        {
            return;
        }
        List<ChartFile> chartList = [.. charts.Where(chart => chart != null && !string.IsNullOrWhiteSpace(chart.InstallDestination))];
        IPrimaryHashLookup existingHashes = owner.CreateInstalledChartKeySnapshotExcludingChartsUnsafe(chartList);
        LibraryMutationDelta mutationDelta = owner.FixInstallationDirectory(
            chartList,
            existingHashes,
            approvedDuplicateRemovalPaths,
            out List<ChartFile> chartsToRemove,
            out List<ChartFile> maintenanceCharts);
        owner.ApplyLibraryMutationDelta(mutationDelta);
        if (chartsToRemove.Count > 0)
        {
            RemoveLibraryChartsCore(chartsToRemove.Select(LibraryChartRef.FromChartFile), sendToRecycleBin: true, approvedWholeFolderDeletePaths: []);
        }
        List<ChartFile> maintenanceTargets = NormalizeResourceMaintenanceTargetCharts(maintenanceCharts);
        if (maintenanceTargets.Count > 0)
        {
            owner.ApplyCatalogMaintenance(
                maintenanceTargets,
                forceUpdate: true,
                resourceHealthMutationReason: "fix_installation_directory");
        }
    }

    private List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts)
    {
        return owner.NormalizeResourceMaintenanceTargetCharts(charts);
    }

    internal void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPaths)
    {
        List<string> paths = [.. (chartPaths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (paths.Count == 0)
        {
            return;
        }
        owner.RemovePendingChartsFromPendingPackagesAndInstallRows(paths);
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
        owner.LogInstallPerformance(message);
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
