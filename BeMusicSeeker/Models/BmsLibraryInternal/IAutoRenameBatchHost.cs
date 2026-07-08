using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Bridges auto rename batch workflow to BMSLibrary state and UI services.
/// </summary>
internal interface IAutoRenameBatchHost
{
    void LogInstallPerformance(string message);

    void ShowDriveRootBmsSkipped();

    void ShowRenameFailed(FolderAutoRenamePlan plan);

    void ShowRenameFolderNotExists(string sourceDirectory);

    string NormalizeAutoRenameFolderName(string folderName);

    bool DirectoryExists(string directoryPath);

    InstallDestinationOverlayChartRefSnapshot CreateInstallDestinationOverlayChartRefSnapshot();

    LibraryMutationDelta BuildFolderMoveDelta(
        string sourceDirectory,
        string destinationDirectory,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts);

    bool TryMoveLibraryChartFolderFileOnly(string sourceDirectory, string destinationDirectory);

    MovedFolderReferenceUpdateResult UpdateMovedFolderReferences(List<LibraryFolderPathChange> movedFolders);

    void LogReverseLookupMutationAndQueueWarmupIfNeeded(
        string reason,
        DirectoryResourceLookupCache.ReverseLookupMutationResult mutationResult);

    void ApplyLibraryMutationDeltaWithPerformanceContext(LibraryMutationDelta delta, string reason);
}
