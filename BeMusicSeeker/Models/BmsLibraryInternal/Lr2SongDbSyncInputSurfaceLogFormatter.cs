using System.Collections.Generic;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongDbSyncInputSurfaceLogFormatter
{
    public static string Format(
        Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        Lr2SongDbSyncDirectoryTargetSelection directoryTargetSelection,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        Lr2SongDbSyncDirectoryEntrySelection directoryEntrySelection,
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates,
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope,
        Lr2SongDbSyncFolderCandidateSelection lr2FolderCandidateSelection,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection,
        IReadOnlyList<string> textFileDirectories,
        Lr2SongDbSyncTextFileDirectorySelection textFileDirectorySelection,
        Lr2SongDbSyncInputSurfaceTimings timings)
    {
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = scanSurfaceSelection.Surface;
        IReadOnlyCollection<string> directoryMetadataTargets = directoryTargetSelection.DirectoryMetadataTargets;
        IReadOnlyCollection<string> lr2FolderParentDirectoryTargets = directoryTargetSelection.Lr2FolderParentDirectoryTargets;
        IReadOnlyCollection<string> directoryEntryTargets = directoryTargetSelection.DirectoryEntryTargets;
        Lr2SongDbSyncPreparedDataSurface pendingPreparedSurface = preparedSurfaceSelection.PendingSurface;
        Lr2SongDbSyncPreparedDataSurface preparedSurface = preparedSurfaceSelection.ActiveSurface;

        return "lr2_song_db_sync_input_surface"
            + " reusedScanSurface=" + scanSurfaceSelection.ReusedScanSurface.ToString().ToLowerInvariant()
            + " scanSurfaceMissReason=" + scanSurfaceSelection.MissReason
            + " scanSurfaceGeneration=" + scanSurfaceSelection.Generation
            + " rootDirs=" + rootSnapshot.RootDirectories.Count
            + " lr2FolderDiscoveryDirs=" + rootSnapshot.Lr2FolderDiscoveryDirectories.Count
            + " lr2BuiltinFolderSourceDirs=" + settingsSnapshot.Lr2BuiltinFolderSourceDirectories.Count
            + " lr2FolderPruneDirs=" + settingsSnapshot.Lr2FolderPruneDirectories.Count
            + " scanSurfaceNormalFolderDirs=" + (scanSurface?.NormalFolderDirectoryPaths?.Count ?? 0)
            + " normalDirectoryTargets=" + directoryMetadataTargets.Count
            + " lr2FolderParentDirectoryTargets=" + lr2FolderParentDirectoryTargets.Count
            + " directoryTargets=" + directoryEntryTargets.Count
            + " directoryEntries=" + directoryEntries.Count
            + " missingDirectoryEntries=" + directoryEntrySelection.MissingCount
            + " directoryEntriesMs=" + timings.DirectoryEntries.ElapsedMilliseconds
            + " folderInfoCandidates=" + folderInfoCandidates.Paths.Count
            + " lr2FolderCandidates=" + lr2FolderFileCandidates.Paths.Count
            + " appManagedScopeDirs=" + appManagedOutputScope.Directories.Count
            + " enumeratedAppManagedFiltered=" + lr2FolderCandidateSelection.EnumeratedAppManagedCandidateCount
            + " enumeratedAppManagedExactFiles=" + lr2FolderCandidateSelection.EnumeratedAppManagedExactFileCount
            + " reusedLr2FolderSurface=" + scanSurfaceSelection.ReusedLr2FolderSurface.ToString().ToLowerInvariant()
            + " hasPreparedSurface=" + preparedSurfaceSelection.HasActivePreparedSurface.ToString().ToLowerInvariant()
            + " hasPreparedLr2FolderSurface=" + preparedSurfaceSelection.HasActiveLr2FolderSurface.ToString().ToLowerInvariant()
            + " preparedSurfaceAlreadyAppliedToScanSurface=" + preparedSurfaceSelection.AlreadyAppliedToScanSurface.ToString().ToLowerInvariant()
            + " preparedSurfaceAppliedScanGeneration=" + preparedSurfaceSelection.AppliedScanGeneration
            + " pendingPreparedScopeDirs=" + (pendingPreparedSurface?.Lr2FolderScopeDirectories?.Count ?? 0)
            + " pendingPreparedLr2FolderCandidates=" + (pendingPreparedSurface?.Lr2FolderFilePaths?.Count ?? 0)
            + " pendingPreparedDirectoryEntries=" + (pendingPreparedSurface?.DirectoryEntries?.Count ?? 0)
            + " pendingPreparedFolderInfoCandidates=" + (pendingPreparedSurface?.FolderInfoFilePaths?.Count ?? 0)
            + " pendingPreparedTextFileDirs=" + (pendingPreparedSurface?.TextFileDirectories?.Count ?? 0)
            + " preparedDirectoryEntries=" + (preparedSurface?.DirectoryEntries?.Count ?? 0)
            + " preparedFolderInfoCandidates=" + (preparedSurface?.FolderInfoFilePaths?.Count ?? 0)
            + " preparedTextFileDirs=" + (preparedSurface?.TextFileDirectories?.Count ?? 0)
            + " lr2FolderDiscoveryComplete=" + lr2FolderFileCandidates.DiscoveryComplete.ToString().ToLowerInvariant()
            + " textFileDirs=" + textFileDirectories.Count
            + " lr2FolderCandidatesSource=" + lr2FolderCandidateSelection.Source
            + " textFileDirsSource=" + textFileDirectorySelection.Source
            + " rowSnapshotMs=" + timings.RowSnapshot.ElapsedMilliseconds
            + " rootsMs=" + timings.Roots.ElapsedMilliseconds
            + " builtinSettingsMs=" + timings.BuiltinSettings.ElapsedMilliseconds
            + " scanSurfaceLookupMs=" + timings.ScanSurface.ElapsedMilliseconds
            + " directoryTargetsMs=" + timings.DirectoryTargets.ElapsedMilliseconds
            + " lr2FolderCandidatesMs=" + timings.Lr2FolderCandidates.ElapsedMilliseconds
            + " folderInfoCandidatesMs=" + timings.FolderInfoCandidates.ElapsedMilliseconds
            + " textFileDirsMs=" + timings.TextFileDirs.ElapsedMilliseconds
            + " totalMs=" + timings.Input.ElapsedMilliseconds;
    }
}
