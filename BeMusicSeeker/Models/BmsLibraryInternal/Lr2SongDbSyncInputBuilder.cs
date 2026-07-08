using System;
using System.Collections.Generic;
using System.Diagnostics;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncInputBuilder(
    Func<
        Lr2SongDbSyncScanSurfaceSnapshot,
        Lr2SongDbSyncInputRootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot,
        Lr2SongDbSyncPreparedSurfaceSelection,
        Lr2SongDbSyncAppManagedOutputScope,
        Lr2SongDbSyncFolderCandidateSelection> createFolderCandidateSelection,
    Func<
        IReadOnlyCollection<string>,
        Lr2FolderFileCandidateSnapshot,
        Lr2SongDbSyncInputRootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot,
        Lr2SongDbSyncDirectoryTargetSelection> createDirectoryTargetSelection,
    Func<
        Lr2SongDbSyncScanSurfaceSnapshot,
        IEnumerable<string>,
        IReadOnlyCollection<string>,
        Lr2SongDbSyncPreparedSurfaceSelection,
        Lr2SongDbSyncFolderInfoCandidateSelection> createFolderInfoCandidateSelection,
    Func<
        Lr2SongDbSyncScanSurfaceSnapshot,
        IEnumerable<string>,
        IReadOnlyCollection<string>,
        Lr2SongDbSyncPreparedSurfaceSelection,
        Lr2SongDbSyncDirectoryEntrySelection> createDirectoryEntrySelection,
    Func<
        Lr2SongDbSyncScanSurfaceSnapshot,
        Lr2TextMetadataCandidateSnapshot,
        Lr2SongDbSyncPreparedSurfaceSelection,
        Lr2SongDbSyncTextFileDirectorySelection> createTextFileDirectorySelection,
    Action<string> logInstallPerformance)
{
    public Lr2SongDbSyncInput Create(
        Lr2SongDbSyncInputRowSnapshot rowSnapshot,
        Lr2SongDbSyncInputRootSnapshot rootSnapshot,
        Lr2SongDbSyncInputSettingsSnapshot settingsSnapshot,
        Lr2SongDbSyncScanSurfaceSelection scanSurfaceSelection,
        IReadOnlyCollection<string> directoryMetadataTargets,
        Lr2SongDbSyncPreparedSurfaceSelection preparedSurfaceSelection,
        Lr2SongDbSyncAppManagedOutputScope appManagedOutputScope,
        Stopwatch inputStopwatch,
        Stopwatch rowSnapshotStopwatch,
        Stopwatch rootsStopwatch,
        Stopwatch builtinSettingsStopwatch,
        Stopwatch scanSurfaceStopwatch,
        Stopwatch directoryTargetsStopwatch,
        Stopwatch lr2FolderCandidatesStopwatch)
    {
        Lr2SongDbSyncScanSurfaceSnapshot scanSurface = scanSurfaceSelection.Surface;
        Lr2SongDbSyncFolderCandidateSelection lr2FolderCandidateSelection =
            createFolderCandidateSelection(
                scanSurface,
                rootSnapshot,
                settingsSnapshot,
                preparedSurfaceSelection,
                appManagedOutputScope);
        Lr2FolderFileCandidateSnapshot lr2FolderFileCandidates = lr2FolderCandidateSelection.Candidates;
        lr2FolderCandidatesStopwatch.Stop();
        Lr2SongDbSyncDirectoryTargetSelection directoryTargetSelection =
            createDirectoryTargetSelection(
                directoryMetadataTargets,
                lr2FolderFileCandidates,
                rootSnapshot,
                settingsSnapshot);
        IReadOnlyCollection<string> directoryEntryTargets = directoryTargetSelection.DirectoryEntryTargets;
        var folderInfoCandidatesStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncFolderInfoCandidateSelection folderInfoCandidateSelection =
            createFolderInfoCandidateSelection(
                scanSurface,
                rootSnapshot.Lr2FolderDiscoveryDirectories,
                directoryEntryTargets,
                preparedSurfaceSelection);
        Lr2FolderInfoCandidateSnapshot folderInfoCandidates = folderInfoCandidateSelection.Candidates;
        Lr2TextMetadataCandidateSnapshot textMetadataCandidates = folderInfoCandidateSelection.TextMetadataCandidates;
        folderInfoCandidatesStopwatch.Stop();
        var directoryEntriesStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncDirectoryEntrySelection directoryEntrySelection =
            createDirectoryEntrySelection(
                scanSurface,
                rootSnapshot.Lr2FolderDiscoveryDirectories,
                directoryEntryTargets,
                preparedSurfaceSelection);
        IReadOnlyDictionary<string, RootFileEnumerationEntry> directoryEntries = directoryEntrySelection.Entries;
        directoryEntriesStopwatch.Stop();
        var textFileDirsStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncTextFileDirectorySelection textFileDirectorySelection =
            createTextFileDirectorySelection(
                scanSurface,
                textMetadataCandidates,
                preparedSurfaceSelection);
        IReadOnlyList<string> textFileDirectories = textFileDirectorySelection.Directories;
        textFileDirsStopwatch.Stop();
        inputStopwatch.Stop();
        var inputSurfaceTimings = new Lr2SongDbSyncInputSurfaceTimings(
            rowSnapshotStopwatch,
            rootsStopwatch,
            builtinSettingsStopwatch,
            scanSurfaceStopwatch,
            directoryTargetsStopwatch,
            directoryEntriesStopwatch,
            lr2FolderCandidatesStopwatch,
            folderInfoCandidatesStopwatch,
            textFileDirsStopwatch,
            inputStopwatch);
        logInstallPerformance(Lr2SongDbSyncInputSurfaceLogFormatter.Format(
            scanSurfaceSelection,
            rootSnapshot,
            settingsSnapshot,
            directoryTargetSelection,
            directoryEntries,
            directoryEntrySelection,
            folderInfoCandidates,
            lr2FolderFileCandidates,
            appManagedOutputScope,
            lr2FolderCandidateSelection,
            preparedSurfaceSelection,
            textFileDirectories,
            textFileDirectorySelection,
            inputSurfaceTimings));

        return Lr2SongDbSyncInputFactory.Create(
            rootSnapshot,
            rowSnapshot,
            directoryTargetSelection,
            folderInfoCandidates,
            directoryEntries,
            settingsSnapshot,
            appManagedOutputScope,
            lr2FolderFileCandidates,
            textFileDirectories,
            scanSurfaceSelection);
    }
}
