using System;
using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Explicit composition boundary consumed by the library file-operation
/// workflow.  The workflow owner does not retain a reference to BMSLibrary.
/// </summary>
internal interface ILibraryFileOperationPort
{
    IDisposable EnterFolderMoveWriteScope();

    IDisposable EnterFolderMoveReadScope();

    IDisposable EnterNormalInvalidExtensionRenameWriteScope();

    IDisposable EnterPendingInvalidExtensionRenameWriteScope();

    IDisposable EnterLibraryChartRemovalWriteScope();

    IDisposable EnterFixInstallationDirectoryWriteScope();

    IDisposable EnterMergeWriteScope(long operationId);

    bool TryBlockMutation(string operation, bool showMessage);

    List<FolderAutoRenamePlan> BuildRootFolderMovePlans(
        IEnumerable<ChartFile> selectedCharts,
        string destinationRootDirectory);

    LibraryMutationDelta BuildFolderMoveDelta(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<ChartFile> sourceCharts,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        bool unregister,
        bool notifyStorageRowPathChanges);

    List<FolderAutoRenamePlan> BuildAutoRenamePlans(
        IEnumerable<ChartFile> selectedCharts,
        IEnumerable<string> rootFolders,
        bool renameRootFolder);

    List<FolderAutoRenamePlan> BuildAutoRenamePlansForSourceFolders(
        string parentDirectory);

    void MoveFolder(
        string sourceDirectory,
        string destinationDirectory);

    LibraryMutationDelta RenameLibraryFileExtensions(
        IEnumerable<ChartFile> charts,
        string newExtension,
        bool unregister);

    PendingExtensionRenameReport RenamePendingBmsFormatChartFileExtensions(
        IEnumerable<ChartFile> charts,
        string newExtension);

    PendingFileDeletionResult DeletePendingCharts(
        IEnumerable<ChartFile> charts,
        bool sendToRecycleBin,
        bool deleteContainingPackageFoldersWhenNoBms);

    LibraryMutationDelta FixInstallationDirectory(
        IEnumerable<ChartFile> charts,
        IPrimaryHashLookup existingHashes,
        IEnumerable<string> approvedDuplicateRemovalChartPaths,
        out List<ChartFile> chartsToRemove,
        out List<ChartFile> maintenanceCharts);

    LibraryMutationDelta PrepareMergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        IEnumerable<ChartFile> sourceCharts,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        string hashSnapshotReason,
        long operationId,
        out bool success,
        out List<ChartFile> preparedSourceCharts,
        out IPrimaryHashLookup existingHashes);

    InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot();

    DirectoryResourceLookupCache.ReverseLookupMutationResult MoveFolderAndUpdateReferences(
        string sourceDirectory,
        string destinationDirectory);

    MovedFolderReferenceUpdateResult UpdateMovedFolderReferences(
        IEnumerable<LibraryFolderPathChange> movedFolders);

    LibraryMutationDelta DeleteLibraryCharts(
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
        out DirectoryResourceLookupCache.ReverseLookupMutationResult resourceIndexMutation);

    DirectoryResourceLookupCache.ReverseLookupMutationResult RemoveReverseLookupDirectoriesUnderSource(string sourceDirectory);

    DirectoryResourceLookupCache.ReverseLookupMutationResult AddReverseLookupDirectories(ChartScanResult scan);

    List<string> getBMSDirectories();

    string NormalizeAutoRenameFolderName(string folderName);

    void RemovePendingChartsFromPendingPackagesAndInstallRows(IEnumerable<string> chartPathsToRemove);

    List<ChartFile> CreateOwnedRealPathChartSnapshotsUnsafe(string directoryPath);

    List<string> CreateOwnedRealPathChartDirectoriesUnsafe(string directoryPath);

    List<ChartFile> CreateOwnedStorageTargetChartSnapshotsForSubtreeDirectoryUnsafe(string directoryPath);

    List<ChartFile> CreateDirectLibraryChartSnapshotsInFolders(IEnumerable<string> folderPaths);

    IPrimaryHashLookup CreateInstalledChartKeySnapshotExcludingChartsUnsafe(
        IEnumerable<ChartFile> excluded,
        string reason = null,
        long operationId = 0L);

    string CreateChartFolderPathFromCharts(IEnumerable<ChartFile> chartFiles, string parentDirectory);

    void ApplyLibraryMutationDelta(LibraryMutationDelta delta);

    void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string performanceLogContext);

    void ApplyCatalogMaintenance(
        IEnumerable<ChartFile> charts,
        bool forceUpdate = false,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode = ResourceHealthIndexUpdateMode.DeltaOnUpdates,
        string resourceHealthMutationReason = null);

    void InvalidateDuplicateChartGroupsCache();

    void InvalidateInstalledDirectoryIndex();

    void LogReverseLookupMutationAndQueueWarmupIfNeeded(
        string reason,
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult);

    void LogInstallPerformance(string message);

    void LogInstallPerformanceWarning(string message);

    UiDialogDefaultResult ShowOperationDialog(
        string message,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult);

    List<ChartFile> NormalizeResourceMaintenanceTargetCharts(IEnumerable<ChartFile> charts);

    IEnumerable<string> GetDuplicateInstallRepairPaths(ChartFile chart);

    bool MoveMergePackageFiles(
        IReadOnlyList<ChartFile> chartSnapshots,
        string sourceDirectory,
        string destinationDirectory,
        IPrimaryHashLookup existingHashes);

}
