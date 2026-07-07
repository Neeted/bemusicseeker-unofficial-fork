using System.Collections.Generic;
using System.Diagnostics;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongDbSyncInputSurfaceLogFormatter
{
    public static string Format(
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface,
        string scanSurfaceMissReason,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        IReadOnlyCollection<string> directoryMetadataTargets,
        IReadOnlyCollection<string> lr2FolderParentDirectoryTargets,
        IReadOnlyCollection<string> directoryEntryTargets,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries,
        Lr2SongDbSyncDirectoryEntrySelection directoryEntrySelection,
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates,
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope,
        Lr2SongDbSyncFolderCandidateSelection lr2FolderCandidateSelection,
        bool reusedLr2FolderSurface,
        bool hasPreparedSurface,
        bool hasPreparedLr2FolderSurface,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection,
        Lr2SongDbSyncPreparedDataSurface pendingPreparedSurface,
        Lr2SongDbSyncPreparedDataSurface preparedSurface,
        IReadOnlyList<string> textFileDirectories,
        Lr2SongDbSyncTextFileDirectorySelection textFileDirectorySelection,
        Stopwatch rowSnapshotStopwatch,
        Stopwatch rootsStopwatch,
        Stopwatch builtinSettingsStopwatch,
        Stopwatch scanSurfaceStopwatch,
        Stopwatch directoryTargetsStopwatch,
        Stopwatch directoryEntriesStopwatch,
        Stopwatch lr2FolderCandidatesStopwatch,
        Stopwatch folderInfoCandidatesStopwatch,
        Stopwatch textFileDirsStopwatch,
        Stopwatch inputStopwatch)
    {
        return "lr2_song_db_sync_input_surface"
            + " reusedScanSurface=" + (scanSurface != null).ToString().ToLowerInvariant()
            + " scanSurfaceMissReason=" + (scanSurfaceMissReason ?? string.Empty)
            + " scanSurfaceGeneration=" + (scanSurface?.Generation ?? 0)
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
            + " directoryEntriesMs=" + directoryEntriesStopwatch.ElapsedMilliseconds
            + " folderInfoCandidates=" + folderInfoCandidates.Paths.Count
            + " lr2FolderCandidates=" + lr2FolderFileCandidates.Paths.Count
            + " appManagedScopeDirs=" + appManagedOutputScope.Directories.Count
            + " enumeratedAppManagedFiltered=" + lr2FolderCandidateSelection.EnumeratedAppManagedCandidateCount
            + " enumeratedAppManagedExactFiles=" + lr2FolderCandidateSelection.EnumeratedAppManagedExactFileCount
            + " reusedLr2FolderSurface=" + reusedLr2FolderSurface.ToString().ToLowerInvariant()
            + " hasPreparedSurface=" + hasPreparedSurface.ToString().ToLowerInvariant()
            + " hasPreparedLr2FolderSurface=" + hasPreparedLr2FolderSurface.ToString().ToLowerInvariant()
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
            + " rowSnapshotMs=" + rowSnapshotStopwatch.ElapsedMilliseconds
            + " rootsMs=" + rootsStopwatch.ElapsedMilliseconds
            + " builtinSettingsMs=" + builtinSettingsStopwatch.ElapsedMilliseconds
            + " scanSurfaceLookupMs=" + scanSurfaceStopwatch.ElapsedMilliseconds
            + " directoryTargetsMs=" + directoryTargetsStopwatch.ElapsedMilliseconds
            + " lr2FolderCandidatesMs=" + lr2FolderCandidatesStopwatch.ElapsedMilliseconds
            + " folderInfoCandidatesMs=" + folderInfoCandidatesStopwatch.ElapsedMilliseconds
            + " textFileDirsMs=" + textFileDirsStopwatch.ElapsedMilliseconds
            + " totalMs=" + inputStopwatch.ElapsedMilliseconds;
    }
}
